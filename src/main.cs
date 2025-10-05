// SPDX-License-Identifier: MIT

namespace Fahrenheit.Tools.DEdit;

internal partial class Program {

    /// <summary>
    ///     Checks which input files, if any, exist, and opens a <see cref="FileInfo"/> for each.
    /// </summary>
    private static List<FileInfo> _args_validate_input_files(ArgumentResult argr) {
        List<FileInfo> input_files = [];

        foreach (Token token in argr.Tokens) {
            string file_path = token.Value;

            if (!File.Exists(file_path)) {
                argr.AddError($"Input file {file_path} does not exist.");
                continue;
            }

            input_files.Add(new FileInfo(file_path));
        }

        return input_files;
    }

    /// <summary>
    ///     Checks whether the output folder exists, and errors out if not.
    /// </summary>
    private static string _args_validate_output_folder(ArgumentResult argr) {
        if (argr.Tokens.Count > 1) {
            argr.AddError("Specified multiple output directories.");
            return argr.Tokens[0].Value;
        }

        string dir_path = argr.Tokens[0].Value;
        if (!Directory.Exists(dir_path)) {
            argr.AddError($"Specified output directory {dir_path} does not exist.");
        }

        return argr.Tokens[0].Value;
    }

    private static int Main(string[] args) {
        Console.WriteLine($"Started with args: {string.Join(' ', args)}\n");

        RootCommand cmd_root = new("Perform various operations on FFX/X-2 dialogue files and character sets.");

        Option<List<FileInfo>> opt_input = new("--input", "-i") {
            Description                    = "Input file(s) to process.",
            Arity                          = ArgumentArity.OneOrMore,
            Required                       = true,
            Recursive                      = true,
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory            = _args_validate_input_files
        };

        Option<string> opt_output = new("--output", "-o") {
            Description         = "What folder to emit outputs to. The folder must already exist.",
            Arity               = ArgumentArity.ExactlyOne,
            Required            = true,
            Recursive           = true,
            DefaultValueFactory = _args_validate_output_folder
        };

        Option<FhLangId> opt_charset = new Option<FhLangId>("--lang", "-l") {
            Description = "Set the language to interpret the input file as.",
            Arity       = ArgumentArity.ExactlyOne,
            Recursive   = true
        };

        cmd_root.Options.Add(opt_input);
        cmd_root.Options.Add(opt_output);
        cmd_root.Options.Add(opt_charset);

        Command cmd_compile_charsets = new("compile-charsets", "Compiles character sets based on the game's SJIS tables.");

        Option<string> opt_charset_namespace = new Option<string>("--namespace", "-ns") {
            Description = "Set the namespace of the resulting C# file(s).",
            Required    = true,
            Arity       = ArgumentArity.ExactlyOne
        };

        cmd_compile_charsets.Options.Add(opt_charset_namespace);
        cmd_root.Subcommands.Add(cmd_compile_charsets);

        cmd_compile_charsets.SetAction(parseResult => _c_compile_charsets(
            parseResult.GetRequiredValue(opt_input),
            parseResult.GetRequiredValue(opt_output),
            parseResult.GetRequiredValue(opt_charset_namespace)
            ));

        ParseResult argparse_result = cmd_root.Parse(args);
        return argparse_result.Invoke();
    }

    /// <summary>
    ///     Performs charset compilation on <paramref name="input_files"/>, emitting them to <paramref name="output_dir"/>.
    /// </summary>
    private static void _c_compile_charsets(List<FileInfo> input_files, string output_dir, string output_namespace) {
        foreach (FileInfo input_file in input_files) {
            string output_path = Path.Join(output_dir, $"{input_file.Name}-{Guid.NewGuid()}.g.cs");

            using (FileStream input_file_stream  = input_file.OpenRead())
            using (FileStream output_file_stream = File.Open(output_path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                using (StreamWriter sw = new StreamWriter(output_file_stream)) {
                    sw.Write(_compile_charset(input_file_stream, output_namespace));
                }
            }

            Console.WriteLine($"{input_file.Name} -> {output_path}.");
        }
    }

    /// <summary>
    ///     For a given encoding table, emits a C# source file that allows the usage of the corresponding charset.
    /// </summary>
    private static string _compile_charset(FileStream input_file, string ns) {
        StringBuilder to_byte = new(); // ToByte switch
        StringBuilder to_char = new(); // ToChar switch

        byte[] input_bytes = new byte[input_file.Length];
        input_file.ReadExactly(input_bytes);

        Encoding   encoding   = Encoding.UTF8;
        string     input_str  = encoding.GetString(input_bytes);
        List<char> duplicates = new List<char>(input_str.Length);

        int char_value = 0x30;
        foreach (char c in input_str) {
            int  duplicate_index = duplicates.IndexOf(c);
            bool is_duplicate    = duplicate_index != -1;

            duplicates.Add(c);

            string utf8_codepoint = Convert.ToHexString(encoding.GetBytes($"{c}"));
            string char_value_hex = char_value.ToString("X");
            string escaped_char   = c switch {
                '\'' => @"'\''",
                '\\' => @"'\\'",
                _    => $"'{c}'"
            }; ;

            to_char.AppendLine(is_duplicate
                ? $"            // 0x{char_value_hex} => {escaped_char}, // duplicate of 0x{(duplicate_index + 0x30):X}"
                : $"            0x{char_value_hex} => {escaped_char}, // {utf8_codepoint}");
            to_byte.AppendLine(is_duplicate
                ? $"            // {escaped_char} => 0x{char_value_hex}, // duplicate of 0x{(duplicate_index + 0x30):X}"
                : $"            {escaped_char} => 0x{char_value_hex}, // {utf8_codepoint}");
            char_value++;
        }

        to_char.AppendLine($"            _ => throw new Exception($\"No character exists for input {{u}}.\"),");
        to_byte.AppendLine($"            _ => throw new Exception($\"Invalid character {{c}} specified.\"),");

        string file_name         = Path.GetFileName(input_file.Name);
        string sjis_table_suffix = file_name[(file_name.IndexOf('_') + 1)..file_name.IndexOf('.')];
        string charset_suffix    = $"{char.ToUpperInvariant(sjis_table_suffix[0])}{sjis_table_suffix[1..]}";

        return $$"""
/* [dedit {{DateTime.UtcNow:dd/M/yy HH:mm}}]
 * This file was generated by Fahrenheit.DEdit (https://github.com/peppy-enterprises/fh-tools-dedit).
 */

namespace {{ns}};

public partial class FhCharsetSelector {
    public static readonly FhCharset{{charset_suffix}} {{charset_suffix}} = new FhCharset{{charset_suffix}}();
}

public sealed class FhCharset{{charset_suffix}} : FhCharset {
    public override ushort encode(char c) {
        return c switch {
{{to_byte}}
        };
    }

    public override char decode(ushort u) {
        return u switch {
{{to_char}}
        };
    }
}
""";
    }
}
