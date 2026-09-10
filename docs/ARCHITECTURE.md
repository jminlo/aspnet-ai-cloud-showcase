# Architecture notes

## AI-assisted form workflow

1. The Razor form collects a toy name and image.
2. JavaScript submits the data to an authenticated MVC endpoint with an anti-forgery token.
3. The controller validates the request and delegates inference to an injected service.
4. The service sends the image and a constrained prompt to the Gemini multimodal API.
5. A response JSON schema limits the model to supported material and category values.
6. The server validates the model response before returning suggestions to the browser.
7. The form displays the proposed values for user review instead of saving them automatically.

## Configuration workflow

The application startup examples load shared settings from Azure Key Vault using `DefaultAzureCredential`. In a deployed Azure environment this supports managed identity, while local development can use a developer credential. Secrets remain outside source control.

## Scope

These files are extracted feature examples. Types from the larger Toy Management System, including shared models and data services, are referenced but intentionally not reproduced here.
