# ASP.NET AI & Cloud Feature Showcase

A curated collection of features I implemented for an academic Toy Management System using ASP.NET Core, the Gemini multimodal API, Azure Key Vault, managed identity, and Azure DevOps.

## Highlights

- Generates structured toy metadata from a name and uploaded image with the Gemini API
- Constrains model output with a JSON schema and validates returned categories and materials
- Integrates the AI workflow into an ASP.NET Core MVC form using AJAX and anti-forgery protection
- Loads centralized application configuration through Azure Key Vault and `DefaultAzureCredential`
- Demonstrates cloud-ready configuration for Student, Teacher, and API applications

## Repository structure

- `src/ai-autofill/` - DTO, service contract, Gemini integration, controller example, and Razor view
- `src/key-vault/` - application startup and project configuration examples
- `docs/` - implementation notes and architecture documentation

## Important context

This repository is a portfolio-oriented feature showcase extracted from a larger team academic project. It contains the components I used to demonstrate my individual development work and is not intended to represent the complete original application. Organization-specific deployment exports and credentials are intentionally excluded.

## Technologies

C#, ASP.NET Core MVC, Razor, JavaScript, REST APIs, Gemini API, Azure Key Vault, Azure Identity, dependency injection, AJAX, JSON schema validation, Azure DevOps

## Configuration

No secrets are stored in this repository. A consuming application must provide:

- `Gemini:ApiKey`
- `Gemini:Model` (optional; defaults to `gemini-2.5-flash`)
- an Azure Key Vault URI through environment-specific configuration

For local development, use .NET user secrets or environment variables. Do not commit credentials.

## Author

Joshua Mina-Loaiza - Computer Science DEC graduate, Cégep Heritage College
