namespace ReviewMemory.Core;

/// <summary>
/// Búsqueda sobre la memoria indexada. Con texto se aplica full-text search;
/// con rutas de archivo se filtran PRs que tocaron esos archivos.
/// Al menos una de las dos señales debe estar presente.
/// </summary>
public sealed record SearchQuery(
    string Text,
    string? Repo = null,
    IReadOnlyList<string>? Paths = null,
    int Limit = 10);
