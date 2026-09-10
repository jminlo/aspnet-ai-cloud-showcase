using TMSTeacher.Models;

namespace TMSTeacher.Services;

public interface IGeminiToySuggestionService
{
    Task<AiToySuggestionDto?> SuggestToyFieldsAsync(string name, IFormFile imageFile);
}
