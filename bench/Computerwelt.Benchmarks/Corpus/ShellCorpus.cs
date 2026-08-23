// The corpus is bashkit's own: `crates/bashkit-bench/src/cases.rs`, case for case and in
// its order, so a number here can be put beside a number from upstream's tables. The
// categories and the expected output are upstream's too — a case whose output stops
// matching is measuring the wrong thing, so the runner checks it rather than trusting it.

namespace Computerwelt.Benchmarks;

/// <summary>The corpus, mirrored from <c>crates/bashkit-bench/src/cases.rs</c>.</summary>
public static class ShellCorpus
{
    /// <summary>Every case, in the order upstream declares them.</summary>
    public static readonly BenchCase[] All =
    [
        // === Startup ===
        new("startup_empty", BenchCategory.Startup, "Empty command (pure startup time)", "true", ""),
        new("startup_true", BenchCategory.Startup, "True command", "true", ""),
        new("startup_echo", BenchCategory.Startup, "Simple echo", "echo hello", "hello\n"),
        new("startup_exit", BenchCategory.Startup, "Exit with code", "exit 0", ""),

        // === Variables ===
        new("var_assign_simple", BenchCategory.Variables, "Simple variable assignment", "x=hello; echo $x", "hello\n"),
        new("var_assign_many", BenchCategory.Variables, "Multiple variable assignments",
            """
            a=1; b=2; c=3; d=4; e=5
            f=6; g=7; h=8; i=9; j=10
            echo "$a$b$c$d$e$f$g$h$i$j"
            """,
            "12345678910\n"),
        new("var_default", BenchCategory.Variables, "Default value expansion", "echo ${UNDEFINED:-default}", "default\n"),
        new("var_length", BenchCategory.Variables, "String length", "x=hello; echo ${#x}", "5\n"),
        new("var_substring", BenchCategory.Variables, "Substring extraction", "x=hello_world; echo ${x:0:5}", "hello\n"),
        new("var_replace", BenchCategory.Variables, "Pattern replacement", "x=hello_world; echo ${x/world/bash}", "hello_bash\n"),
        new("var_nested", BenchCategory.Variables, "Nested variable expansion",
            """
            a=inner
            inner=value
            echo ${!a}
            """,
            "value\n"),
        new("var_export", BenchCategory.Variables, "Export and use", "export FOO=bar; echo $FOO", "bar\n"),

        // === Arithmetic ===
        new("arith_basic", BenchCategory.Arithmetic, "Basic arithmetic", "echo $((1 + 2))", "3\n"),
        new("arith_complex", BenchCategory.Arithmetic, "Complex arithmetic expression", "echo $((10 * 5 + 3 - 2 / 1))", "51\n"),
        new("arith_variables", BenchCategory.Arithmetic, "Arithmetic with variables", "x=10; y=20; echo $((x + y * 2))", "50\n"),
        new("arith_increment", BenchCategory.Arithmetic, "Increment operations", "x=5; ((x++)); ((x++)); echo $x", "7\n"),
        new("arith_modulo", BenchCategory.Arithmetic, "Modulo operation", "echo $((17 % 5))", "2\n"),
        new("arith_loop_sum", BenchCategory.Arithmetic, "Sum in loop",
            """
            sum=0
            for i in 1 2 3 4 5 6 7 8 9 10; do
                sum=$((sum + i))
            done
            echo $sum
            """,
            "55\n"),

        // === Control ===
        new("ctrl_if_simple", BenchCategory.Control, "Simple if statement", "if true; then echo yes; fi", "yes\n"),
        new("ctrl_if_else", BenchCategory.Control, "If-else statement", "if false; then echo no; else echo yes; fi", "yes\n"),
        new("ctrl_for_list", BenchCategory.Control, "For loop over list", "for i in a b c d e; do echo -n $i; done; echo", "abcde\n"),
        new("ctrl_for_range", BenchCategory.Control, "For loop with range",
            """
            for ((i=0; i<5; i++)); do
                echo -n $i
            done
            echo
            """,
            "01234\n"),
        new("ctrl_while", BenchCategory.Control, "While loop",
            """
            i=0
            while [ $i -lt 5 ]; do
                echo -n $i
                i=$((i + 1))
            done
            echo
            """,
            "01234\n"),
        new("ctrl_case", BenchCategory.Control, "Case statement",
            """
            x=two
            case $x in
                one) echo 1 ;;
                two) echo 2 ;;
                *) echo other ;;
            esac
            """,
            "2\n"),
        new("ctrl_function", BenchCategory.Control, "Function definition and call",
            """
            greet() {
                echo "Hello, $1!"
            }
            greet World
            """,
            "Hello, World!\n"),
        new("ctrl_function_return", BenchCategory.Control, "Function with return value",
            """
            add() {
                echo $(($1 + $2))
            }
            result=$(add 3 4)
            echo $result
            """,
            "7\n"),
        new("ctrl_nested_loops", BenchCategory.Control, "Nested loops",
            """
            for i in 1 2 3; do
                for j in a b c; do
                    echo -n "$i$j "
                done
            done
            echo
            """,
            "1a 1b 1c 2a 2b 2c 3a 3b 3c \n"),

        // === Strings ===
        new("str_concat", BenchCategory.Strings, "String concatenation",
            """
            a="Hello"
            b="World"
            echo "$a $b"
            """,
            "Hello World\n"),
        new("str_printf", BenchCategory.Strings, "Printf formatting", "printf '%s=%d\\n' name 42", "name=42\n"),
        new("str_printf_pad", BenchCategory.Strings, "Printf with padding", "printf '%05d\\n' 42", "00042\n"),
        new("str_echo_escape", BenchCategory.Strings, "Echo with escapes", "echo -e 'line1\\nline2'", "line1\nline2\n"),
        new("str_prefix_strip", BenchCategory.Strings, "Strip prefix", "x=/path/to/file.txt; echo ${x##*/}", "file.txt\n"),
        new("str_suffix_strip", BenchCategory.Strings, "Strip suffix", "x=file.txt; echo ${x%.txt}", "file\n"),
        new("str_uppercase", BenchCategory.Strings, "Uppercase conversion", "x=hello; echo ${x^^}", "HELLO\n"),
        new("str_lowercase", BenchCategory.Strings, "Lowercase conversion", "x=HELLO; echo ${x,,}", "hello\n"),

        // === Arrays ===
        new("arr_create", BenchCategory.Arrays, "Array creation", "arr=(a b c); echo ${arr[0]}", "a\n"),
        new("arr_all", BenchCategory.Arrays, "Array all elements", "arr=(one two three); echo ${arr[@]}", "one two three\n"),
        new("arr_length", BenchCategory.Arrays, "Array length", "arr=(a b c d e); echo ${#arr[@]}", "5\n"),
        new("arr_iterate", BenchCategory.Arrays, "Array iteration",
            """
            arr=(apple banana cherry)
            for item in "${arr[@]}"; do
                echo "$item"
            done
            """,
            "apple\nbanana\ncherry\n"),
        new("arr_slice", BenchCategory.Arrays, "Array slicing", "arr=(1 2 3 4 5); echo ${arr[@]:1:3}", "2 3 4\n"),
        new("arr_assign_index", BenchCategory.Arrays, "Array index assignment", "arr=(); arr[0]=a; arr[2]=c; echo ${arr[@]}", "a c\n"),

        // === Pipes ===
        new("pipe_simple", BenchCategory.Pipes, "Simple pipe", "echo hello | cat", "hello\n"),
        new("pipe_multi", BenchCategory.Pipes, "Multi-stage pipe", "echo 'a b c' | cat | cat | cat", "a b c\n"),
        new("pipe_command_subst", BenchCategory.Pipes, "Command substitution", "result=$(echo hello); echo $result", "hello\n"),
        new("pipe_heredoc", BenchCategory.Pipes, "Here document",
            """
            cat <<EOF
            line1
            line2
            EOF
            """,
            "line1\nline2\n"),
        new("pipe_herestring", BenchCategory.Pipes, "Here string", "cat <<< 'hello world'", "hello world\n"),
        new("pipe_discard", BenchCategory.Pipes, "Discard output via pipe", "result=$(echo test); echo ok", "ok\n"),

        // === Tools ===
        new("tool_grep_simple", BenchCategory.Tools, "Grep simple pattern", "echo -e 'apple\\nbanana\\napricot' | grep 'ap'", "apple\napricot\n"),
        new("tool_grep_case", BenchCategory.Tools, "Grep case insensitive", "echo -e 'Apple\\nBANANA\\napple' | grep -i 'apple'", "Apple\napple\n"),
        new("tool_grep_count", BenchCategory.Tools, "Grep count matches", "echo -e 'a\\nb\\na\\nc\\na' | grep -c 'a'", "3\n"),
        new("tool_grep_invert", BenchCategory.Tools, "Grep invert match", "echo -e 'yes\\nno\\nyes' | grep -v 'no'", "yes\nyes\n"),
        new("tool_grep_regex", BenchCategory.Tools, "Grep extended regex", "echo -e 'cat\\ndog\\ncot' | grep -E 'c[ao]t'", "cat\ncot\n"),
        new("tool_sed_replace", BenchCategory.Tools, "Sed simple replace", "echo 'hello world' | sed 's/world/bash/'", "hello bash\n"),
        new("tool_sed_global", BenchCategory.Tools, "Sed global replace", "echo 'aaa' | sed 's/a/b/g'", "bbb\n"),
        new("tool_sed_delete", BenchCategory.Tools, "Sed delete line", "echo -e 'a\\nb\\nc' | sed '/b/d'", "a\nc\n"),
        new("tool_sed_lines", BenchCategory.Tools, "Sed line range", "echo -e '1\\n2\\n3\\n4\\n5' | sed -n '2,4p'", "2\n3\n4\n"),
        new("tool_sed_backrefs", BenchCategory.Tools, "Sed with backreferences", "echo 'hello' | sed 's/\\(hel\\)lo/\\1p/'", "help\n"),
        new("tool_awk_print", BenchCategory.Tools, "Awk print field", "echo 'a b c' | awk '{print $2}'", "b\n"),
        new("tool_awk_sum", BenchCategory.Tools, "Awk sum column", "echo -e '1\\n2\\n3\\n4\\n5' | awk '{sum+=$1} END {print sum}'", "15\n"),
        new("tool_awk_pattern", BenchCategory.Tools, "Awk pattern match", "echo -e 'apple 1\\nbanana 2\\napricot 3' | awk '/^a/ {print $2}'", "1\n3\n"),
        new("tool_awk_fieldsep", BenchCategory.Tools, "Awk field separator", "echo 'a:b:c' | awk -F: '{print $2}'", "b\n"),
        new("tool_awk_nf", BenchCategory.Tools, "Awk number of fields", "echo 'one two three four' | awk '{print NF}'", "4\n"),
        new("tool_awk_compute", BenchCategory.Tools, "Awk arithmetic", "echo '10 20' | awk '{print $1 + $2, $1 * $2}'", "30 200\n"),
        new("tool_jq_identity", BenchCategory.Tools, "Jq identity", "echo '{\"a\":1}' | jq '.'", "{\n  \"a\": 1\n}\n"),
        new("tool_jq_field", BenchCategory.Tools, "Jq field access", "echo '{\"name\":\"test\",\"value\":42}' | jq '.value'", "42\n"),
        new("tool_jq_array", BenchCategory.Tools, "Jq array access", "echo '[1,2,3,4,5]' | jq '.[2]'", "3\n"),
        new("tool_jq_filter", BenchCategory.Tools, "Jq filter array", "echo '[1,2,3,4,5]' | jq '[.[] | select(. > 2)]'", "[\n  3,\n  4,\n  5\n]\n"),
        new("tool_jq_map", BenchCategory.Tools, "Jq map", "echo '[1,2,3]' | jq '[.[] * 2]'", "[\n  2,\n  4,\n  6\n]\n"),

        // === Complex ===
        new("complex_fibonacci", BenchCategory.Complex, "Fibonacci sequence (recursive)",
            """
            fib() {
                local n=$1
                if [ $n -le 1 ]; then
                    echo $n
                else
                    local a=$(fib $((n-1)))
                    local b=$(fib $((n-2)))
                    echo $((a + b))
                fi
            }
            fib 10
            """,
            "55\n"),
        new("complex_fibonacci_iter", BenchCategory.Complex, "Fibonacci sequence (iterative)",
            """
            a=0
            b=1
            for i in 1 2 3 4 5 6 7 8 9 10; do
                c=$((a + b))
                a=$b
                b=$c
            done
            echo $a
            """,
            "55\n"),
        new("complex_nested_subst", BenchCategory.Complex, "Nested command substitution", "\necho $(echo $(echo $(echo deep)))\n", "deep\n"),
        new("complex_loop_compute", BenchCategory.Complex, "Loop with computation",
            """
            sum=0
            for i in 1 2 3 4 5 6 7 8 9 10; do
                sq=$((i * i))
                sum=$((sum + sq))
            done
            echo $sum
            """,
            "385\n"),
        new("complex_string_build", BenchCategory.Complex, "String building in loop",
            """
            result=""
            for c in a b c d e; do
                result="$result$c"
            done
            echo $result
            """,
            "abcde\n"),
        new("complex_json_transform", BenchCategory.Complex, "JSON transformation", "\necho '[{\"name\":\"alice\",\"score\":85},{\"name\":\"bob\",\"score\":92}]' | jq '.[0].name'\n", "\"alice\"\n"),
        new("complex_pipeline_text", BenchCategory.Complex, "Text pipeline processing", "\necho -e \"apple\\nbanana\\napricot\\ncherry\" | grep \"^a\" | sed 's/a/A/g'\n", "Apple\nApricot\n"),

        // === Large ===
        new("large_loop_1000", BenchCategory.Large, "Counting loop to 1000",
            """
            sum=0
            for ((i=1; i<=1000; i++)); do
                sum=$((sum + i))
            done
            echo $sum
            """,
            "500500\n"),
        new("large_string_append_100", BenchCategory.Large, "String append 100 times",
            """
            s=""
            for ((i=0; i<100; i++)); do
                s="${s}x"
            done
            echo ${#s}
            """,
            "100\n"),
        new("large_array_fill_200", BenchCategory.Large, "Fill array with 200 elements",
            """
            arr=()
            for ((i=0; i<200; i++)); do
                arr+=($i)
            done
            echo ${#arr[@]}
            """,
            "200\n"),
        new("large_nested_loops", BenchCategory.Large, "Nested loops 20x20",
            """
            sum=0
            for ((i=0; i<20; i++)); do
                for ((j=0; j<20; j++)); do
                    sum=$((sum + i * j))
                done
            done
            echo $sum
            """,
            "36100\n"),
        new("large_fibonacci_12", BenchCategory.Large, "Recursive fibonacci(12)",
            """
            fib() {
                local n=$1
                if [ $n -le 1 ]; then
                    echo $n
                else
                    local a=$(fib $((n-1)))
                    local b=$(fib $((n-2)))
                    echo $((a + b))
                fi
            }
            fib 12
            """,
            "144\n"),
        new("large_function_calls_500", BenchCategory.Large, "500 function calls",
            """
            inc() { echo $(($1 + 1)); }
            n=0
            for ((i=0; i<500; i++)); do
                n=$(inc $n)
            done
            echo $n
            """,
            "500\n"),
        new("large_multiline_script", BenchCategory.Large, "50-line multi-operation script",
            """
            # Variable assignments
            a=hello; b=world; c="$a $b"
            d=1; e=2; f=$((d + e))

            # Array operations
            arr=(alpha beta gamma delta epsilon)
            result=""
            for item in "${arr[@]}"; do
                upper=${item^^}
                result="$result$upper "
            done

            # Arithmetic sequence
            sum=0
            for ((i=1; i<=50; i++)); do
                sum=$((sum + i * i))
            done

            # String processing
            text="The quick brown fox jumps over the lazy dog"
            words=0
            for w in $text; do
                words=$((words + 1))
            done

            # Conditional logic
            if [ $sum -gt 1000 ]; then
                status="large"
            else
                status="small"
            fi

            # Output
            echo "vars: $c $f"
            echo "array: $result"
            echo "sum: $sum"
            echo "words: $words"
            echo "status: $status"
            """,
            "vars: hello world 3\narray: ALPHA BETA GAMMA DELTA EPSILON \nsum: 42925\nwords: 9\nstatus: large\n"),
        new("large_pipeline_chain", BenchCategory.Large, "Multi-stage pipeline with data generation",
            """
            for ((i=1; i<=100; i++)); do
                echo "line $i value $((i * 7 % 31))"
            done | grep "value [12][0-9]" | sed 's/line /L/g' | awk '{sum+=$3} END {print NR, sum}'
            """,
            null),
        new("large_assoc_array", BenchCategory.Large, "Associative array operations",
            """
            declare -A m
            m[name]=alice
            m[age]=30
            m[city]=nyc
            echo "${m[name]} ${m[age]} ${m[city]}"
            """,
            "alice 30 nyc\n"),

        // === Subshell ===
        new("subshell_simple", BenchCategory.Subshell, "Simple subshell", "(echo hello)", "hello\n"),
        new("subshell_isolation", BenchCategory.Subshell, "Subshell variable isolation",
            """
            x=outer
            (x=inner; echo $x)
            echo $x
            """,
            "inner\nouter\n"),
        new("subshell_nested", BenchCategory.Subshell, "Nested subshells 4 deep", "\necho $(echo $(echo $(echo $(echo deep))))\n", "deep\n"),
        new("subshell_pipeline", BenchCategory.Subshell, "Subshell in pipeline", "\n(echo -e \"c\\na\\nb\") | sort\n", "a\nb\nc\n"),
        new("subshell_capture_loop", BenchCategory.Subshell, "Command substitution in loop",
            """
            result=""
            for i in 1 2 3 4 5; do
                val=$(echo $((i * i)))
                result="$result $val"
            done
            echo $result
            """,
            "1 4 9 16 25\n"),
        new("subshell_process_subst", BenchCategory.Subshell, "Process substitution with diff-like comparison",
            """
            a=$(echo -e "1\n2\n3")
            b=$(echo -e "1\n2\n3")
            if [ "$a" = "$b" ]; then
                echo same
            else
                echo different
            fi
            """,
            "same\n"),

        // === Io ===
        new("io_redirect_write", BenchCategory.Io, "Write to file and read back",
            """
            echo "hello world" > bench_test.txt
            cat bench_test.txt
            rm bench_test.txt
            """,
            "hello world\n"),
        new("io_append", BenchCategory.Io, "Append to file",
            """
            echo "line1" > bench_append.txt
            echo "line2" >> bench_append.txt
            echo "line3" >> bench_append.txt
            cat bench_append.txt
            rm bench_append.txt
            """,
            "line1\nline2\nline3\n"),
        new("io_dev_null", BenchCategory.Io, "Redirect to /dev/null",
            """
            echo "discarded" > /dev/null
            echo "kept"
            """,
            "kept\n"),
        new("io_stderr_redirect", BenchCategory.Io, "Stderr redirect",
            """
            echo "out"
            echo "err" >&2
            """,
            "out\n"),
        new("io_read_lines", BenchCategory.Io, "Read lines from here-doc",
            """
            count=0
            while IFS= read -r line; do
                count=$((count + 1))
            done <<EOF
            alpha
            beta
            gamma
            delta
            epsilon
            EOF
            echo $count
            """,
            "5\n"),
        new("io_multiline_heredoc", BenchCategory.Io, "Large heredoc processing",
            """
            cat <<'EOF' | wc -l
            line1
            line2
            line3
            line4
            line5
            line6
            line7
            line8
            line9
            line10
            EOF
            """,
            "10\n"),
    ];
}
