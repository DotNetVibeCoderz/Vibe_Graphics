using System.Text;

namespace ThreeAppGen.Services.Conversion;

/// <summary>Markdown written next to the converted solution.</summary>
public static class ConversionReport
{
    public static string Build(
        ConversionRequest request,
        ThreeJsProject project,
        SceneInventory inventory,
        ScaffoldResult scaffold,
        BuildOutcome finalBuild,
        bool usedLlm,
        bool fellBack,
        int fixAttempts,
        string llmNotes,
        IReadOnlyList<string> warnings,
        AppSettings settings)
    {
        StringBuilder md = new();
        md.AppendLine($"# Conversion report / Laporan konversi: {request.ProjectName}");
        md.AppendLine();
        md.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by **Three.Net App Generator** (Jack - The Code Bender).");
        md.AppendLine();
        md.AppendLine("## Summary / Ringkasan");
        md.AppendLine();
        md.AppendLine("| | |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Source / Sumber | `{request.SourceFolder}` |");
        md.AppendLine($"| Target | **{request.Target}** |");
        md.AppendLine($"| Three.js version | {project.ThreeVersion ?? "unknown"} |");
        md.AppendLine($"| Entry script | `{project.EntryScript?.RelativePath ?? "not found"}` |");
        md.AppendLine($"| Scripts / assets | {project.Scripts.Count} / {project.Assets.Count} |");
        md.AppendLine($"| Conversion | {(usedLlm ? $"static inventory + LLM ({settings.ActiveProvider} / {settings.Active.Model})" : "static inventory (baseline)")} |");
        md.AppendLine($"| Auto-fix rounds | {fixAttempts} |");
        md.AppendLine($"| Fell back to baseline | {(fellBack ? "yes, the LLM output did not compile (kept in `llm-attempt/`)" : "no")} |");
        md.AppendLine($"| Build | {(finalBuild.Succeeded ? "succeeded" : $"FAILED ({finalBuild.Errors.Count} errors)")} |");
        md.AppendLine();

        md.AppendLine("## Solution layout / Struktur solusi");
        md.AppendLine();
        foreach (GeneratedProject generated in scaffold.Projects)
        {
            md.AppendLine($"- `{Path.GetRelativePath(scaffold.Root, generated.ProjectPath).Replace('\\', '/')}` - {generated.Role}{(generated.Required ? string.Empty : " (best effort build)")}");
        }

        md.AppendLine();
        md.AppendLine("## Run / Menjalankan");
        md.AppendLine();
        md.AppendLine("```bash");
        md.AppendLine($"dotnet run --project {Path.GetRelativePath(scaffold.Root, scaffold.Startup.ProjectPath).Replace('\\', '/')}");
        md.AppendLine("```");
        md.AppendLine();

        if (request.Target != ConversionTarget.Desktop)
        {
            md.AppendLine("> **Web / Mobile:** the Avalonia heads are generated and compiled when the workload is installed.");
            md.AppendLine("> Running them needs the Three.Net native core for that platform (WebAssembly / Android), which is on");
            md.AppendLine("> the roadmap (phase 5). Use the `Preview` head to run the same UI on the desktop today.");
            md.AppendLine();
        }

        md.AppendLine("## What was converted / Yang dikonversi");
        md.AppendLine();
        md.AppendLine($"- Geometries: {inventory.Geometries.Count} ({string.Join(", ", inventory.Geometries.Select(g => g.SourceType).Distinct())})");
        md.AppendLine($"- Materials: {inventory.Materials.Count} ({string.Join(", ", inventory.Materials.Select(m => m.SourceType).Distinct())})");
        md.AppendLine($"- Meshes: {inventory.Objects.Count(o => o.Kind == SceneObjectKind.Mesh)}, groups: {inventory.Objects.Count(o => o.Kind == SceneObjectKind.Group)}");
        md.AppendLine($"- Lights: {string.Join(", ", inventory.Objects.Where(o => o.Kind == SceneObjectKind.Light).Select(o => o.SourceType))}");
        md.AppendLine($"- Camera: {inventory.Camera?.SourceType ?? "default"}");
        md.AppendLine($"- Textures: {inventory.Textures.Count}, models: {inventory.Objects.Count(o => o.Kind == SceneObjectKind.Model)}");
        md.AppendLine($"- Animations recognised statically: {inventory.Animations.Count}");
        md.AppendLine($"- Post-processing: tone mapping {inventory.ToneMapping}, exposure {inventory.Exposure}, bloom {(inventory.Bloom ? "on" : "off")}");
        md.AppendLine($"- Detected features: {string.Join(", ", project.DetectedFeatures)}");
        md.AppendLine();

        if (inventory.Unsupported.Count > 0)
        {
            md.AppendLine("## Needs manual work / Perlu dikerjakan manual");
            md.AppendLine();
            foreach (string item in inventory.Unsupported)
            {
                md.AppendLine($"- {item}");
            }

            md.AppendLine();
        }

        if (inventory.Notes.Count > 0)
        {
            md.AppendLine("## Approximations / Pendekatan");
            md.AppendLine();
            foreach (string note in inventory.Notes.Distinct())
            {
                md.AppendLine($"- {note}");
            }

            md.AppendLine();
        }

        if (llmNotes.Trim().Length > 0)
        {
            md.AppendLine("## Notes from the model / Catatan dari LLM");
            md.AppendLine();
            md.AppendLine(llmNotes.Trim());
            md.AppendLine();
        }

        if (!finalBuild.Succeeded)
        {
            md.AppendLine("## Remaining build errors");
            md.AppendLine();
            foreach (BuildDiagnostic error in finalBuild.Errors.Take(50))
            {
                md.AppendLine($"- `{error}`");
            }

            md.AppendLine();
        }

        if (warnings.Count > 0)
        {
            md.AppendLine("## Warnings");
            md.AppendLine();
            foreach (string warning in warnings.Distinct().Take(80))
            {
                md.AppendLine($"- {warning}");
            }

            md.AppendLine();
        }

        md.AppendLine("---");
        md.AppendLine("Three.Net - dibuat oleh Gravicode Studios, dipimpin Kang Fadhil / made by Gravicode Studios, led by Kang Fadhil.");
        return md.ToString();
    }

    public static string BuildReadme(ConversionRequest request, ScaffoldResult scaffold)
    {
        string run = Path.GetRelativePath(scaffold.Root, scaffold.Startup.ProjectPath).Replace('\\', '/');
        return $"""
            # {request.ProjectName}

            Converted from a Three.js web project with **Three.Net App Generator**.
            Dikonversi dari project web Three.js menggunakan **Three.Net App Generator**.

            ## English

            - Target: **{request.Target}**
            - The converted scene lives in `src/{request.ProjectName}.Core/ConvertedScene.cs`; assets are in `src/{request.ProjectName}.Core/Assets`.
            - Run: `dotnet run --project {run}`
            - Details, approximations and manual follow-ups: [CONVERSION_REPORT.md](CONVERSION_REPORT.md)

            ## Bahasa Indonesia

            - Target: **{request.Target}**
            - Scene hasil konversi ada di `src/{request.ProjectName}.Core/ConvertedScene.cs`; aset ada di `src/{request.ProjectName}.Core/Assets`.
            - Menjalankan: `dotnet run --project {run}`
            - Detail, pendekatan dan pekerjaan lanjutan: [CONVERSION_REPORT.md](CONVERSION_REPORT.md)

            ---
            Three.Net - dibuat oleh Gravicode Studios, dipimpin Kang Fadhil.
            """;
    }
}
