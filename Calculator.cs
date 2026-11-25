using System.Globalization;
using System.Numerics;

namespace ESearch;

internal static class Calculator
{
    internal static bool TryCalculate(string input, out string result)
    {
        result = "";
        if (input.AsSpan().IndexOfAny("+-*/×÷()^") < 0) return false;
        input = input.Trim();
        // 普通四则保留 decimal 精度。含幂、科学函数、常量或科学计数法时使用 double。
        bool scientific = input.Any(c => c == '^' || char.IsAsciiLetter(c) || c is 'π' or 'Π');
        return scientific
            ? TryEvaluate<double>(input, "G15", out result)
            : TryEvaluate<decimal>(input, "0.############################", out result);
    }

    private static bool TryEvaluate<T>(string input, string format, out string result) where T : struct, INumber<T>
    {
        result = "";
        int position = 0;
        char Peek()
        {
            while (position < input.Length && char.IsWhiteSpace(input[position])) position++;
            return position < input.Length ? input[position] : '\0';
        }

        // 逐步拒绝非有限中间值，避免后续运算将无效结果变回普通数字。
        static T? Finite(T? value) => value.HasValue && T.IsFinite(value.Value) ? value : null;

        // 每层先读乘除项，再从左至右合并加减，保证同级运算的结合顺序。
        T? ReadSum(int depth)
        {
            T? value = ReadProduct(depth);
            while (value.HasValue && Peek() is ('+' or '-'))
            {
                char op = input[position++];
                T? right = ReadProduct(depth);
                value = Finite(op == '+' ? value + right : value - right);
            }
            return value;
        }

        T? ReadProduct(int depth)
        {
            T? value = ReadOperand(depth);
            while (value.HasValue && Peek() is ('*' or '/' or '×' or '÷'))
            {
                char op = input[position++];
                T? right = ReadOperand(depth);
                value = Finite(op is '*' or '×' ? value * right : value / right);
            }
            return value;
        }

        T? ReadOperand(int depth)
        {
            // 括号、函数嵌套和右结合的幂链共用深度上限，保护 UI 线程栈。
            if (depth > 64) return null;
            bool negative = false;
            while (Peek() is '+' or '-')
                if (input[position++] == '-') negative = !negative;

            T? value = ReadAtom(depth);
            // 先算幂再应用前缀符号。指数递归读取，允许 2^-3 和 2^3^2。
            if (value.HasValue && Peek() == '^')
            {
                position++;
                T? exponent = ReadOperand(depth + 1);
                if (!exponent.HasValue) return null;
                value = Finite(T.CreateChecked(Math.Pow(double.CreateChecked(value.Value), double.CreateChecked(exponent.Value))));
            }
            return negative ? -value : value;
        }

        T? ReadAtom(int depth)
        {
            char first = Peek();
            if (first == '(') return ReadGroup(depth);
            if (char.IsAsciiLetter(first) || first is 'π' or 'Π')
            {
                int start = position++;
                while (position < input.Length && char.IsAsciiLetterOrDigit(input[position])) position++;
                string name = input[start..position].ToLowerInvariant();
                if (name is "pi" or "π") return T.CreateChecked(Math.PI);
                if (name == "e") return T.CreateChecked(Math.E);
                if (Peek() != '(') return null;
                T? argument = ReadGroup(depth);
                if (!argument.HasValue) return null;
                double x = double.CreateChecked(argument.Value);
                // 角度先归约再计算，整象限能得到精确的零；正切在奇数个直角处无定义。
                double angle = (x % 360) / 180;
                double answer = name switch
                {
                    "sin" => double.SinPi(angle),
                    "cos" => double.CosPi(angle),
                    "tan" when Math.Abs(x % 180) != 90 => double.TanPi(angle),
                    "sqrt" => Math.Sqrt(x),
                    "abs" => Math.Abs(x),
                    "log" or "log10" => Math.Log10(x),
                    "ln" => Math.Log(x),
                    "exp" => Math.Exp(x),
                    _ => double.NaN
                };
                return Finite(T.CreateChecked(answer));
            }

            int numberStart = position;
            while (position < input.Length && char.IsAsciiDigit(input[position])) position++;
            if (position < input.Length && input[position] == '.')
            {
                position++;
                while (position < input.Length && char.IsAsciiDigit(input[position])) position++;
            }
            if (position < input.Length && input[position] is 'e' or 'E')
            {
                position++;
                if (position < input.Length && input[position] is '+' or '-') position++;
                while (position < input.Length && char.IsAsciiDigit(input[position])) position++;
            }
            // 单独的数值（含 1e-3）仍可作为文件名或词条搜索。
            if (numberStart == 0 && position == input.Length) return null;
            var token = input.AsSpan(numberStart, position - numberStart);
            return T.TryParse(token, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out T number) ? Finite(number) : null;
        }

        T? ReadGroup(int depth)
        {
            position++; // 调用者已确认左括号。
            T? value = ReadSum(depth + 1);
            if (Peek() != ')') return null;
            position++;
            return value;
        }

        try
        {
            T? value = ReadSum(0);
            if (!value.HasValue || Peek() != '\0' || position != input.Length) return false;
            result = T.IsZero(value.Value) ? "0" : value.Value.ToString(format, CultureInfo.InvariantCulture);
            return true;
        }
        catch (ArithmeticException) // 十进制除零或溢出时，沿用普通搜索。
        {
            return false;
        }
    }

}
