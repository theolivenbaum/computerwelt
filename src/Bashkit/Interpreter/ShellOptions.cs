namespace Bashkit.Interpreter;

/// <summary>
/// The <c>set</c> and <c>shopt</c> flags that change evaluation.
/// </summary>
/// <remarks>
/// These are read on nearly every expansion and command, so they live as fields rather
/// than in a dictionary lookup.
/// </remarks>
public sealed class ShellOptions
{
    /// <summary><c>set -e</c> — exit on an unhandled non-zero status.</summary>
    public bool ErrExit { get; set; }

    /// <summary><c>set -u</c> — expanding an unset variable is an error.</summary>
    public bool NoUnset { get; set; }

    /// <summary><c>set -x</c> — trace commands to stderr before running them.</summary>
    public bool XTrace { get; set; }

    /// <summary><c>set -v</c> — echo input lines as they are read.</summary>
    public bool Verbose { get; set; }

    /// <summary><c>set -f</c> — disable pathname expansion.</summary>
    public bool NoGlob { get; set; }

    /// <summary><c>set -o pipefail</c> — a pipeline fails if any stage fails.</summary>
    public bool PipeFail { get; set; }

    /// <summary><c>set -n</c> — read commands but do not run them.</summary>
    public bool NoExec { get; set; }

    /// <summary><c>set -o noclobber</c> — <c>&gt;</c> will not truncate an existing file.</summary>
    public bool NoClobber { get; set; }

    /// <summary><c>set -a</c> — every assignment is also exported.</summary>
    public bool AllExport { get; set; }

    /// <summary><c>shopt -s nullglob</c> — a glob with no matches expands to nothing.</summary>
    public bool NullGlob { get; set; }

    /// <summary><c>shopt -s failglob</c> — a glob with no matches is an error.</summary>
    public bool FailGlob { get; set; }

    /// <summary><c>shopt -s dotglob</c> — globs match leading dots.</summary>
    public bool DotGlob { get; set; }

    /// <summary><c>shopt -s nocaseglob</c> — globs match case-insensitively.</summary>
    public bool NoCaseGlob { get; set; }

    /// <summary><c>shopt -s nocasematch</c> — <c>case</c> and <c>[[ ]]</c> match case-insensitively.</summary>
    public bool NoCaseMatch { get; set; }

    /// <summary><c>shopt -s extglob</c> — enable the <c>?()</c> family of pattern operators.</summary>
    public bool ExtGlob { get; set; }

    /// <summary><c>shopt -s globstar</c> — <c>**</c> crosses directory boundaries.</summary>
    public bool GlobStar { get; set; }

    /// <summary><c>shopt -s expand_aliases</c>. On by default here, unlike an interactive bash.</summary>
    public bool ExpandAliases { get; set; } = true;

    /// <summary><c>shopt -s inherit_errexit</c>.</summary>
    public bool InheritErrExit { get; set; }

    /// <summary>Copies the options, for a subshell that must not leak its changes back.</summary>
    public ShellOptions Clone() => (ShellOptions)MemberwiseClone();

    /// <summary>Reads a <c>set -o</c> option by name.</summary>
    public bool? GetByName(string name) => name switch
    {
        "errexit" => ErrExit,
        "nounset" => NoUnset,
        "xtrace" => XTrace,
        "verbose" => Verbose,
        "noglob" => NoGlob,
        "pipefail" => PipeFail,
        "noexec" => NoExec,
        "noclobber" => NoClobber,
        "allexport" => AllExport,
        "nullglob" => NullGlob,
        "failglob" => FailGlob,
        "dotglob" => DotGlob,
        "nocaseglob" => NoCaseGlob,
        "nocasematch" => NoCaseMatch,
        "extglob" => ExtGlob,
        "globstar" => GlobStar,
        "expand_aliases" => ExpandAliases,
        "inherit_errexit" => InheritErrExit,
        _ => null,
    };

    /// <summary>Sets a <c>set -o</c> option by name. Returns false for an unknown name.</summary>
    public bool SetByName(string name, bool value)
    {
        switch (name)
        {
            case "errexit": ErrExit = value; return true;
            case "nounset": NoUnset = value; return true;
            case "xtrace": XTrace = value; return true;
            case "verbose": Verbose = value; return true;
            case "noglob": NoGlob = value; return true;
            case "pipefail": PipeFail = value; return true;
            case "noexec": NoExec = value; return true;
            case "noclobber": NoClobber = value; return true;
            case "allexport": AllExport = value; return true;
            case "nullglob": NullGlob = value; return true;
            case "failglob": FailGlob = value; return true;
            case "dotglob": DotGlob = value; return true;
            case "nocaseglob": NoCaseGlob = value; return true;
            case "nocasematch": NoCaseMatch = value; return true;
            case "extglob": ExtGlob = value; return true;
            case "globstar": GlobStar = value; return true;
            case "expand_aliases": ExpandAliases = value; return true;
            case "inherit_errexit": InheritErrExit = value; return true;
            default: return false;
        }
    }

    /// <summary>Maps a single-letter <c>set</c> flag to its long name.</summary>
    public static string? LongNameForFlag(char flag) => flag switch
    {
        'e' => "errexit",
        'u' => "nounset",
        'x' => "xtrace",
        'v' => "verbose",
        'f' => "noglob",
        'n' => "noexec",
        'C' => "noclobber",
        'a' => "allexport",
        _ => null,
    };
}
