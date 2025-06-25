/* [fkelava 17/5/23 02:48]
 * A shitty, quick tool to emit (mostly?!) valid C# from swidx C header.
 *
 * Only and specifically used to convert #defines to C# enums for constant imports.
 */

using System;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Text;

using Fahrenheit.Core;
using Fahrenheit.Core.FFX;

namespace Fahrenheit.Tools.DEdit;

internal enum FhDEditMode {
    ReadCharsets = 1,
    Compile      = 2,
    Decompile    = 3
}

internal class Program {
    static void Main(string[] args) {
        Console.WriteLine($"{nameof(Fahrenheit)}.{nameof(DEdit)}\n");
        Console.WriteLine($"Started with args: {string.Join(' ', args)}\n");

        Option<FhDEditMode> opt_mode    = new Option<FhDEditMode>("--mode") { Description = "Select the DEdit operating mode." };
        Option<string>      opt_ns      = new Option<string>     ("--ns"  ) { Description = "Set the namespace of the resulting C# file, if reading a charset." };
        Option<string>      opt_src     = new Option<string>     ("--src" ) { Description = "Set the path to the source file." };
        Option<string>      opt_dest    = new Option<string>     ("--dest") { Description = "Set the folder where the C#/text file should be written." };
        Option<FhLangId>    opt_charset = new Option<FhLangId>   ("--cs"  ) { Description = "Set the charset that should be used for the input file." };

        opt_mode.Required = true;
        opt_src .Required = true;
        opt_dest.Required = true;

        RootCommand root_cmd = new RootCommand("Perform various operations on FFX dialogue files and character sets.") {
            opt_mode,
            opt_ns,
            opt_src,
            opt_dest,
            opt_charset
        };

        ParseResult argparse_result = root_cmd.Parse(args);

        FhDEditMode mode              = argparse_result.GetValue(opt_mode);
        string      src_path          = argparse_result.GetValue(opt_src)  ?? "";
        string      dest_path         = argparse_result.GetValue(opt_dest) ?? "";
        string      default_namespace = argparse_result.GetValue(opt_ns)   ?? "";
        FhLangId    lang_id           = argparse_result.GetValue(opt_charset);

        Stopwatch perfSwatch = Stopwatch.StartNew();

        if (!File.Exists(src_path))
            throw new Exception("E_INVALID_PATH");

        switch (mode) {
            case FhDEditMode.ReadCharsets: DEditReadCharset(src_path, dest_path, default_namespace); break;
            case FhDEditMode.Decompile: DEditDecompile(lang_id, src_path, dest_path); break;
        }

        perfSwatch.Stop();
        Console.WriteLine($"PERF: Operation complete in {perfSwatch.ElapsedMilliseconds} ms");
        return;
    }

    static void DEditReadCharset(string src_path, string dest_path, string? default_namespace = default) {
        string sfn = Path.GetFileName(src_path);
        string dfn = Path.Join(dest_path, $"{sfn}-{Guid.NewGuid()}.g.cs");
        string ns  = default_namespace ?? throw new Exception("FH_E_MISSING_NAMESPACE: Specify --ns at the command line.");
        string cs  = FhCharsetGenerator.EmitCharset(src_path, ns);

        using (FileStream fs = File.Open(dfn, FileMode.CreateNew)) {
            using (StreamWriter sw = new StreamWriter(fs)) {
                sw.Write(cs);
            }
        }

        Console.WriteLine(cs);
        Console.WriteLine($"Charset {sfn}: Output is at {dfn}.");
    }

    static void DEditDecompile(FhLangId lang_id, string src_path, string dest_path) {
        string sfn       = Path.GetFileName(src_path);
        bool   isMDict   = sfn == "macrodic.dcp";
        string dfnSuffix = isMDict ? "FFX_MACRODICT" : "FFX_DIALOGUE";
        string dfn       = Path.Join(dest_path, $"{sfn}-{Guid.NewGuid()}.{dfnSuffix}");

        ReadOnlySpan<byte> dialogue = File.ReadAllBytes(src_path);

        string diastr = isMDict
            ? DEditDecompileMacroDict(dialogue, lang_id)
            : DEditDecompileDialogue(dialogue, lang_id);

        using (FileStream fs = File.Open(dfn, FileMode.CreateNew)) {
            using (StreamWriter sw = new StreamWriter(fs)) {
                sw.Write(diastr);
            }
        }

        Console.WriteLine(diastr);
        Console.WriteLine($"{(isMDict ? "Macro dictionary" : "Dialogue")} {sfn}: Output is at {dfn}.");
    }

    static string DEditDecompileDialogue(in ReadOnlySpan<byte> dialogue, FhLangId cs) {
        int               idxCount = dialogue.GetDialogueIndexCount();
        FhDialogueIndex[] idxArray = new FhDialogueIndex[idxCount];

        dialogue.ReadDialogueIndices(in idxArray, out int readCount);

        if (readCount != idxCount) throw new Exception("E_MARSHAL_FAULT");

        return dialogue.ReadDialogue(cs, idxArray);
    }

    static string DEditDecompileMacroDict(in ReadOnlySpan<byte> dialogue, FhLangId cs) {
        FhMacroDictHeader header = dialogue.GetMacroDictHeader();
        StringBuilder     sb     = new StringBuilder();

        unsafe {
            for (int i = 0; i < FhMacroDictHeader.MD_SECTION_NR; i++) {
                sb.AppendLine($"\n--- SECTION {i} ---");

                int                offset = header.SectionOffsets[i];
                ReadOnlySpan<byte> slice  = dialogue[offset..];

                int                idxCount = slice.GetMacroDictIndexCount();
                FhMacroDictIndex[] idxArray = new FhMacroDictIndex[idxCount];

                slice.ReadMacroDictIndices(in idxArray, out int readCount);

                if (readCount != idxCount) throw new Exception("E_MARSHAL_FAULT");

                sb.Append(slice.ReadMacroDict(cs, idxArray));
                sb.AppendLine($"--- END SECTION {i} ---\n");
            }
        }

        return sb.ToString();
    }
}
