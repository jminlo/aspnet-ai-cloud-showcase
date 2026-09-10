using System;
using System.Text;
using System.Text.Json;
using TMSTeacher.Models;

namespace TMSTeacher.Services;

public class GeminiToySuggestionService : IGeminiToySuggestionService
{
    private static readonly string[] AllowedMaterials = {
        "Wood", "Plastic", "Plush", "Metal", "Rubber"
    };

    private static readonly string[] AllowedCategories = {
        "Cognitive",
        "Social and Emotional (Dramatic Play)",
        "Sensory",
        "Physical"
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public GeminiToySuggestionService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<AiToySuggestionDto?> SuggestToyFieldsAsync(string name, IFormFile imageFile)
    {
        var apiKey = _configuration["Gemini:ApiKey"];
        var model = _configuration["Gemini:Model"] ?? "gemini-2.5-flash";

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Gemini API key is not configured");
        }

        await using var ms = new MemoryStream();
        await imageFile.CopyToAsync(ms);

        var mimeType = string.IsNullOrWhiteSpace(imageFile.ContentType)
            ? "image/jpeg"
            : imageFile.ContentType;
        
        var prompt = $"""
            Classify this daycare toy from its name and image.

            Use the provided JSON schema.
            If unsure for material or category, return an empty string.
            Description should be 1 or 2 neutral daycare-appropriate sentences.
            Focus the description on the toy's visible physical appearance only.
            Describe what the toy looks like in the image, similar to an alt text for the toy itself.
            Mention visible traits such as animal/object type, color, texture, shape, size impression, clothing, or notable features.
            Do not describe the background, lighting, camera angle, or surface around the toy.
            Do not describe what the toy is for, how it is used, or any developmental benefit unless that is visually obvious from the toy itself.

            Toy name: {name}
            """;

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt },
                        new
                        {
                            inline_data = new
                            {
                                mime_type = mimeType,
                                data = Convert.ToBase64String(ms.ToArray())
                            }
                        }
                    }
                }
            },
            generationConfig = new
            {
                thinkingConfig = new
                {
                    thinkingBudget = 0
                },
                temperature = 0.2,
                maxOutputTokens = 200,
                responseMimeType = "application/json",
                responseJsonSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        material = new
                        {
                            type = "string",
                            @enum = new[] { "Wood", "Plastic", "Plush", "Metal", "Rubber", "" }
                        },
                        category = new
                        {
                            type = "string",
                            @enum = new[] { "Cognitive", "Social and Emotional (Dramatic Play)", "Sensory", "Physical", "" }
                        },
                        description = new
                        {
                            type = "string"
                        }
                    },
                    required = new[] { "material", "category", "description" }
                }
            }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");

        request.Headers.Add("x-goog-api-key", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Gemini request failed: {raw}");
        }

        using var doc = JsonDocument.Parse(raw);

        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            throw new Exception($"Gemini returned no candidates: {raw}");
        }

        var parts = candidates[0]
            .GetProperty("content")
            .GetProperty("parts");

        string? text = null;
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var textElement))
            {
                text = textElement.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new Exception($"Gemini returned an empty text payload: {raw}");
        }

        var cleaned = text.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("```json", "", StringComparison.OrdinalIgnoreCase)
                .Replace("```", "", StringComparison.Ordinal)
                .Trim();
        }

        var jsonStart = cleaned.IndexOf('{');
        var jsonEnd = cleaned.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            cleaned = cleaned.Substring(jsonStart, jsonEnd - jsonStart + 1);
        }

        AiToySuggestionDto? result;
        try
        {
            result = JsonSerializer.Deserialize<AiToySuggestionDto>(cleaned, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            throw new Exception("Gemini returned invalid JSON.", ex);
        }

        if (result == null)
        {
            throw new Exception("Gemini returned null JSON content.");
        }

        if (!AllowedMaterials.Contains(result.Material))
        {
            result.Material = string.Empty;
        }

        if (!AllowedCategories.Contains(result.Category))
        {
            result.Category = string.Empty;
        }

        result.Description = result.Description?.Trim() ?? string.Empty;

        return result;
    }
}
