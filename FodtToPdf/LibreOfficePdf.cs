using System.Diagnostics;

namespace FodtToPdf;

/// <summary>
/// Converts a filled .fodt document to PDF with headless LibreOffice.
/// The converter is independent of LogicaSAP and Aspose.
///
/// Usage:
///     byte[] pdf = LibreOfficePdf.ConvertToPdf(fodtBytes);
/// or:
///     byte[] pdf = QuestPdfFodtRenderer.RenderWithLibreOffice(fodtBytes);
/// </summary>
public static class LibreOfficePdf
{
    private static Func<byte[], bool, byte[]> _generadorQuestPdf;

    /// <summary>Gets whether QuestPdfFodtRenderer has registered a QuestPDF generator.</summary>
    public static bool HasQuestPdfGenerator => _generadorQuestPdf != null;

    /// <summary>
    /// Registers the QuestPDF generator used by <see cref="ConvertToPdf"/>.
    /// The FodtToPdf.QuestPdf assembly registers it with QuestPdfFodtRenderer.UseQuestPdf().
    /// </summary>
    public static void SetQuestPdfGenerator(Func<byte[], bool, byte[]> generator)
    {
        _generadorQuestPdf = generator;
    }

    /// <summary>Clears the QuestPDF generator and falls back to LibreOffice.</summary>
    public static void ClearQuestPdfGenerator()
    {
        _generadorQuestPdf = null;
    }

    /// <summary>
    /// Generates the PDF with QuestPDF when a generator has been registered; otherwise
    /// uses LibreOffice.
    /// </summary>
    public static byte[] ConvertToPdf(byte[] fodtBytes, bool nestedTables = false)
    {
        if (_generadorQuestPdf != null)
            return _generadorQuestPdf(fodtBytes, nestedTables);

        return ConvertWithLibreOffice(fodtBytes);
    }

    /// <summary>Converts a .fodt document to PDF with headless LibreOffice.</summary>
    public static byte[] ConvertWithLibreOffice(byte[] fodtBytes, int timeoutSeconds = 120)
    {
        if (fodtBytes == null || fodtBytes.Length == 0)
            throw new ArgumentException("The .fodt document is empty.", nameof(fodtBytes));

        if (timeoutSeconds <= 0)
            timeoutSeconds = 120;

        var raiz = Path.Combine(Path.GetTempPath(), "FodtToPdf_LO_" + Guid.NewGuid().ToString("N"));
        var trabajo = Path.Combine(raiz, "trabajo");
        var perfil = Path.Combine(raiz, "perfil");
        var entrada = Path.Combine(trabajo, "documento.fodt");
        var salida = Path.Combine(trabajo, "documento.pdf");

        Directory.CreateDirectory(trabajo);
        Directory.CreateDirectory(perfil);
        File.WriteAllBytes(entrada, fodtBytes);

        try
        {
            Convertir(entrada, trabajo, perfil, raiz, timeoutSeconds);

            if (!File.Exists(salida) || new FileInfo(salida).Length == 0)
                throw new InvalidOperationException("LibreOffice no generó el PDF.");

            return File.ReadAllBytes(salida);
        }
        finally
        {
            try { Directory.Delete(raiz, true); }
            catch (IOException) { /* LibreOffice puede dejar un handle; el temporal se limpia luego */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Convertir(string entrada, string trabajo, string perfil, string raiz, int tiempoLimiteSegundos)
    {
        var soffice = BuscarSoffice();

        var psi = new ProcessStartInfo
        {
            FileName = soffice,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = raiz
        };

        // Aislar el perfil de LibreOffice para que dos conversiones no se pisen.
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--norestore");
        psi.ArgumentList.Add("--nodefault");
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("--invisible");
        psi.ArgumentList.Add("--nolockcheck");
        psi.ArgumentList.Add("--convert-to");
        psi.ArgumentList.Add("pdf");
        psi.ArgumentList.Add("--outdir");
        psi.ArgumentList.Add(trabajo);
        psi.ArgumentList.Add(entrada);
        psi.ArgumentList.Add("-env:UserInstallation=" + new Uri(perfil + Path.DirectorySeparatorChar).AbsoluteUri);

        // TMP/TEMP sí: LibreOffice los usa para archivos temporales. USERPROFILE no,
        // porque en algunos servidores rompe la carga del documento.
        psi.Environment["TMP"] = raiz;
        psi.Environment["TEMP"] = raiz;
        psi.Environment["TMPDIR"] = raiz;

        using var proceso = new Process { StartInfo = psi };
        proceso.Start();

        var stdout = proceso.StandardOutput.ReadToEnd();
        var stderr = proceso.StandardError.ReadToEnd();

        if (!proceso.WaitForExit(tiempoLimiteSegundos * 1000))
        {
            try { proceso.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"LibreOffice no terminó en {tiempoLimiteSegundos} s.");
        }

        if (proceso.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"LibreOffice terminó con código {proceso.ExitCode}." +
                Environment.NewLine + stdout + Environment.NewLine + stderr);
        }
    }

    private static string BuscarSoffice()
    {
        var variable = Environment.GetEnvironmentVariable("SOFFICE_PATH");

        if (!string.IsNullOrWhiteSpace(variable) && File.Exists(variable))
            return variable;

        var candidatos = new[]
        {
            @"C:\Program Files\LibreOffice\program\soffice.com",
            @"C:\Program Files\LibreOffice\program\soffice.exe",
            @"C:\Program Files (x86)\LibreOffice\program\soffice.com",
            @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
            @"C:\Program Files\OpenOffice 4\program\soffice.exe",
            @"C:\Program Files (x86)\OpenOffice 4\program\soffice.exe"
        };

        foreach (var candidato in candidatos)
            if (File.Exists(candidato))
                return candidato;

        foreach (var carpeta in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var nombre in new[] { "soffice.com", "soffice.exe" })
            {
                try
                {
                    var candidato = Path.Combine(carpeta.Trim(), nombre);
                    if (File.Exists(candidato))
                        return candidato;
                }
                catch (ArgumentException)
                {
                    // entrada inválida del PATH
                }
            }
        }

        throw new FileNotFoundException(
            "No se encontró LibreOffice/OpenOffice. Instálalo o define SOFFICE_PATH " +
            @"con la ruta completa de soffice.com (por ejemplo C:\Program Files\LibreOffice\program\soffice.com).");
    }
}
