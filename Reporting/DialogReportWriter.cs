using System.Text;
using System.Text.Json;

namespace BgTtsVoicePatcher.Reporting;

/// <summary>One row of the dialog report: everything about a single StrRef that's
/// already sitting in dialog.tlk (and optionally cross-referenced against DLG/CRE
/// data) - no synthesis, no writes, just a read-only snapshot for browsing/filtering.</summary>
public sealed record DialogReportRow(
    int StrRef,
    string? SystemName,
    string? RealName,
    string Gender,
    bool HasSound,
    string? SoundResRef,
    bool? SoundFileExists,
    string Text);

public static class DialogReportWriter
{
    public static void WriteCsv(IEnumerable<DialogReportRow> rows, string path)
    {
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        writer.WriteLine("StrRef,SystemName,RealName,Gender,HasSound,SoundResRef,SoundFileExists,Text");

        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",",
                row.StrRef.ToString(),
                Escape(row.SystemName),
                Escape(row.RealName),
                Escape(row.Gender),
                row.HasSound ? "true" : "false",
                Escape(row.SoundResRef),
                row.SoundFileExists is { } exists ? (exists ? "true" : "false") : "",
                Escape(row.Text)));
        }
    }

    public static void WriteJson(IEnumerable<DialogReportRow> rows, string path)
    {
        var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        var needsQuoting = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        return needsQuoting ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
