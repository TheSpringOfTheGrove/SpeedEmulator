namespace SpeedEmulator.Services;

public interface IPrintPdfService
{
    Task<string> GeneratePreviewAsync(PrintRenderContext context);

    Task ExportAsync(PrintRenderContext context, string path);
}

