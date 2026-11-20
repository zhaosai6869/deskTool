using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskApp.Utils;

/// <summary>
/// 腾讯动漫（ac.qq.com）阅读页解析。
///
/// 腾讯现在把章节图片列表加密后塞进页面变量 <c>var DATA = '...'</c>，另有一段
/// <c>window["no"+"nce"] = ... (+eval("算术")).toString() ...</c> 的 JS 生成 nonce。
/// 页面 JS 用 nonce 从 DATA 里按位置剔除干扰字符，再做自定义 Base64 解码，得到
/// 包含 picture 数组的 JSON。静态 HTML 里只有第一张预加载图，旧的选择器/img 正则
/// 已经抓不到，必须走这套解密。
///
/// 这里用纯 C# 复刻：轻量求值 nonce 里的算术表达式（不引入 WebView2/JS 引擎），
/// 再按同一规则剔除字符 + 自定义 Base64 解码 + 读 JSON。
/// </summary>
public static class TencentComicSession
{
    private const string KeyStr = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=";

    // 匹配 var DATA = '....'（容忍空格/引号差异）
    private static readonly Regex DataRegex = new(@"var\s+DATA\s*=\s*'(?<d>[^']+)'", RegexOptions.Compiled);
    // 匹配 window["..."] = ...; 的赋值语句
    private static readonly Regex WindowAssignRegex = new(@"window\s*\[[^\r\n]*?;", RegexOptions.Compiled);
    // nonce 串里的拼接片段：字符串字面量 或 (+eval("算术")).toString()
    private static readonly Regex NoncePartRegex = new(
        @"(?<str>""[^""]*""|'[^']*')|(?<eval>\(\s*\+\s*eval\(\s*""(?<expr>[^""]*)""\s*\)\s*\)\s*\.toString\(\s*\))",
        RegexOptions.Compiled);
    // nonce 里的删除指令：数字+字母，如 29a、14852485eb
    private static readonly Regex RemoveRegex = new(@"\d+[a-zA-Z]+", RegexOptions.Compiled);

    /// <summary>
    /// 从阅读页 HTML 解析出该章节的图片 URL 列表。失败返回空列表。
    /// </summary>
    public static List<string> ParseImages(string html)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(html)) return result;

        try
        {
            var dataMatch = DataRegex.Match(html);
            if (!dataMatch.Success) return result;
            var data = dataMatch.Groups["d"].Value;

            // 找到含 eval 的那条 window[...] 赋值（第一条是不含 eval 的 window["no"+"nce"]=...）
            string? nonceAssign = null;
            foreach (Match m in WindowAssignRegex.Matches(html))
            {
                if (m.Value.IndexOf("eval", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    nonceAssign = m.Value;
                    break;
                }
            }
            if (nonceAssign is null) return result;

            var nonce = EvalNonce(nonceAssign);
            if (string.IsNullOrEmpty(nonce)) return result;

            // 按 nonce 指令从 DATA 中剔除干扰字符（倒序，避免位置偏移）
            var chars = new List<char>(data);
            var removals = new List<Match>();
            foreach (Match rm in RemoveRegex.Matches(nonce)) removals.Add(rm);
            // 必须从后往前删，否则前面的删除会让后面的位置索引偏移
            for (var ri = removals.Count - 1; ri >= 0; ri--)
            {
                var token = removals[ri].Value;
                var digits = Regex.Match(token, @"\d+").Value;
                if (!int.TryParse(digits, out var loc)) continue;
                var letters = Regex.Replace(token, @"\d+", "");
                loc &= 255;
                if (loc < 0 || loc >= chars.Count) continue;
                var len = Math.Min(letters.Length, chars.Count - loc);
                chars.RemoveRange(loc, len);
            }

            var decoded = DecodeBase64Custom(new string(chars.ToArray()));
            var json = Encoding.UTF8.GetString(decoded);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("picture", out var pics)) return result;

            foreach (var pic in pics.EnumerateArray())
            {
                if (!pic.TryGetProperty("url", out var u)) continue;
                var url = u.GetString();
                if (!string.IsNullOrWhiteSpace(url)) result.Add(url);
            }
            return result;
        }
        catch
        {
            return result;
        }
    }

    /// <summary>求值 nonce 赋值语句：字符串字面量原样拼接，(+eval("算术")).toString() 求出数字再转字符串。</summary>
    private static string EvalNonce(string assign)
    {
        // 只求值 "=" 右侧；左侧 window["no"+"nce"] 的字符串片段不能算进去
        var eq = assign.IndexOf('=');
        var rhs = eq >= 0 ? assign[(eq + 1)..] : assign;
        var sb = new StringBuilder();
        foreach (Match m in NoncePartRegex.Matches(rhs))
        {
            if (m.Groups["str"].Success)
            {
                var s = m.Groups["str"].Value;
                sb.Append(s.Length >= 2 ? s.Substring(1, s.Length - 2) : string.Empty);
            }
            else if (m.Groups["expr"].Success)
            {
                var expr = m.Groups["expr"].Value;
                try
                {
                    var val = new JsArithmeticEval(expr).Eval();
                    sb.Append(NumberToString(val));
                }
                catch
                {
                    // 单个表达式求值失败就跳过该片段，尽力而为
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>JS number 转字符串：整数不带小数点（与 toString() 一致）。</summary>
    private static string NumberToString(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsInfinity(v)) return v > 0 ? "Infinity" : "-Infinity";
        if (Math.Abs(v - Math.Round(v)) < 1e-9) return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>自定义 Base64：字母表 ABC...+/=，逐 4 字符解码（等价标准 base64，这里按站点算法复刻）。</summary>
    private static byte[] DecodeBase64Custom(string text)
    {
        var result = new List<byte>(text.Length * 3 / 4);
        var i = 0;
        while (i < text.Length)
        {
            if (i + 3 >= text.Length) break;
            var b = KeyStr.IndexOf(text[i]); i++;
            var d = KeyStr.IndexOf(text[i]); i++;
            var f = KeyStr.IndexOf(text[i]); i++;
            var g = KeyStr.IndexOf(text[i]); i++;
            if (b < 0 || d < 0 || f < 0 || g < 0) break;

            var o1 = (b << 2) | (d >> 4);
            var o2 = ((d & 15) << 4) | (f >> 2);
            var o3 = ((f & 3) << 6) | g;
            result.Add((byte)o1);
            if (f != 64) result.Add((byte)o2);
            if (g != 64) result.Add((byte)o3);
        }
        return result.ToArray();
    }

    /// <summary>
    /// 轻量 JS 算术表达式求值器，覆盖腾讯 nonce 里出现的语法：
    /// 四则运算、括号、位与 &amp;、比较 &lt;=、三元 ?:、逻辑非 !、一元正负、
    /// parseInt(...)、'a'.charCodeAt()、window.XXX(视为 NaN)。
    /// </summary>
    private sealed class JsArithmeticEval
    {
        private readonly string _s;
        private int _pos;

        public JsArithmeticEval(string s) { _s = s; }
        public double Eval() => Ternary();

        private void Ws() { while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++; }
        private bool Str(string x) => _pos + x.Length <= _s.Length && string.CompareOrdinal(_s, _pos, x, 0, x.Length) == 0;
        private void Skip(string x) { if (Str(x)) _pos += x.Length; }
        private bool Eat(char c) { Ws(); if (_pos < _s.Length && _s[_pos] == c) { _pos++; return true; } return false; }
        private static double Truthy(double v) => (double.IsNaN(v) || v == 0) ? 0 : 1;

        private double Ternary()
        {
            var cond = Compare();
            Ws();
            if (Eat('?'))
            {
                var a = Ternary();
                Ws();
                Eat(':');
                var b = Ternary();
                return Truthy(cond) != 0 ? a : b;
            }
            return cond;
        }

        private double Compare()
        {
            var l = BitAnd();
            Ws();
            foreach (var op in new[] { "<=", ">=", "==", "!=", "<", ">" })
            {
                if (Str(op))
                {
                    _pos += op.Length;
                    var r = BitAnd();
                    return op switch
                    {
                        "<=" => l <= r ? 1 : 0,
                        ">=" => l >= r ? 1 : 0,
                        "==" => Math.Abs(l - r) < 1e-9 ? 1 : 0,
                        "!=" => Math.Abs(l - r) >= 1e-9 ? 1 : 0,
                        "<" => l < r ? 1 : 0,
                        _ => l > r ? 1 : 0
                    };
                }
            }
            return l;
        }

        private double BitAnd()
        {
            var l = Add();
            while (true)
            {
                Ws();
                if (!Str("&") || Str("&&")) break;
                _pos++;
                var r = Add();
                l = (int)l & (int)r;
            }
            return l;
        }

        private double Add()
        {
            var l = Mul();
            while (true)
            {
                Ws();
                if (_pos >= _s.Length || (_s[_pos] != '+' && _s[_pos] != '-')) break;
                var op = _s[_pos++];
                var r = Mul();
                l = op == '+' ? l + r : l - r;
            }
            return l;
        }

        private double Mul()
        {
            var l = Unary();
            while (true)
            {
                Ws();
                if (_pos >= _s.Length || (_s[_pos] != '*' && _s[_pos] != '/')) break;
                var op = _s[_pos++];
                var r = Unary();
                l = op == '*' ? l * r : l / r;
            }
            return l;
        }

        private double Unary()
        {
            Ws();
            if (_pos < _s.Length && _s[_pos] == '!') { _pos++; return Truthy(Unary()) == 0 ? 1 : 0; }
            if (_pos < _s.Length && _s[_pos] == '+') { _pos++; return Unary(); }
            if (_pos < _s.Length && _s[_pos] == '-') { _pos++; return -Unary(); }
            // ~~x：JS 里两次按位取反 = 截断为整数（对 NaN/Infinity 视为 0）
            if (Str("~~")) { _pos += 2; var v = Unary(); return double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Truncate(v); }
            return Primary();
        }

        private double Primary()
        {
            Ws();
            if (Eat('(')) { var v = Ternary(); Eat(')'); return v; }

            if (Str("parseInt"))
            {
                _pos += "parseInt".Length;
                Eat('(');
                var v = Ternary();
                Eat(')');
                return double.IsNaN(v) ? double.NaN : Math.Truncate(v);
            }

            // Math.pow / Math.round / Math.floor / Math.ceil / Math.abs / Math.max / Math.min
            if (Str("Math."))
            {
                _pos += "Math.".Length;
                var ns = _pos;
                while (_pos < _s.Length && char.IsLetter(_s[_pos])) _pos++;
                var fn = _s.Substring(ns, _pos - ns);
                Eat('(');
                var args = new List<double>();
                if (!Eat(')'))
                {
                    do { args.Add(Ternary()); Ws(); } while (Eat(','));
                    Eat(')');
                }
                var a0 = args.Count > 0 ? args[0] : 0;
                var a1 = args.Count > 1 ? args[1] : 0;
                return fn switch
                {
                    "pow" => Math.Pow(a0, a1),
                    "round" => Math.Floor(a0 + 0.5),
                    "floor" => Math.Floor(a0),
                    "ceil" => Math.Ceiling(a0),
                    "abs" => Math.Abs(a0),
                    "max" => args.Count > 0 ? args.Max() : double.NaN,
                    "min" => args.Count > 0 ? args.Min() : double.NaN,
                    _ => a0
                };
            }

            // document.xxx 是对象（truthy=1）；window.xxx 多为 undefined（NaN，使 !NaN=1）。
            if (Str("document"))
            {
                _pos += "document".Length;
                while (_pos < _s.Length && (_s[_pos] == '.' || char.IsLetterOrDigit(_s[_pos]) || _s[_pos] == '_')) _pos++;
                // 跳过可能的函数调用括号及参数（如 getElementsByTagName('html')）
                if (Eat('('))
                {
                    var depth = 1;
                    while (_pos < _s.Length && depth > 0)
                    {
                        if (_s[_pos] == '(') depth++;
                        else if (_s[_pos] == ')') depth--;
                        _pos++;
                    }
                }
                return 1; // truthy 对象
            }
            if (Str("window"))
            {
                _pos += "window".Length;
                while (_pos < _s.Length && (_s[_pos] == '.' || char.IsLetterOrDigit(_s[_pos]) || _s[_pos] == '_')) _pos++;
                return double.NaN; // undefined
            }

            // 字符串字面量 + 可能的方法（charCodeAt / substring / slice / charAt / length）
            if (_pos < _s.Length && (_s[_pos] == '\'' || _s[_pos] == '"'))
            {
                var q = _s[_pos++];
                var st = _pos;
                while (_pos < _s.Length && _s[_pos] != q) _pos++;
                var str = st <= _pos ? _s.Substring(st, _pos - st) : string.Empty;
                if (_pos < _s.Length) _pos++;
                Ws();

                if (Str(".charCodeAt"))
                {
                    _pos += ".charCodeAt".Length; Eat('(');
                    var idx = 0;
                    if (!Eat(')'))
                    {
                        var iv = Ternary(); Eat(')');
                        idx = double.IsNaN(iv) ? 0 : (int)iv;
                    }
                    return idx >= 0 && idx < str.Length ? str[idx] : double.NaN;
                }
                if (Str(".substring") || Str(".slice"))
                {
                    var isSub = Str(".substring");
                    _pos += (isSub ? ".substring" : ".slice").Length; Eat('(');
                    var a0 = 0;
                    if (!Eat(')'))
                    {
                        var v0 = Ternary(); a0 = double.IsNaN(v0) ? 0 : (int)v0;
                        int? a1v = null;
                        if (Eat(',')) { var v1 = Ternary(); a1v = double.IsNaN(v1) ? null : (int)v1; }
                        Eat(')');
                        if (isSub)
                        {
                            a0 = Math.Clamp(a0, 0, str.Length);
                            var end = a1v.HasValue ? Math.Clamp(a1v.Value, 0, str.Length) : str.Length;
                            if (end < a0) (a0, end) = (end, a0);
                            str = str.Substring(a0, end - a0);
                        }
                        else
                        {
                            if (a0 < 0) a0 = str.Length + a0;
                            a0 = Math.Clamp(a0, 0, str.Length);
                            var end = a1v.HasValue ? (a1v.Value < 0 ? str.Length + a1v.Value : a1v.Value) : str.Length;
                            end = Math.Clamp(end, a0, str.Length);
                            str = str.Substring(a0, end - a0);
                        }
                    }
                    // 结果字符串按 JS + 运算转数字（nonce 里最终都会 +eval 转数值）
                    return double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var sv) ? sv : double.NaN;
                }
                if (Str(".charAt"))
                {
                    _pos += ".charAt".Length; Eat('(');
                    var iv = Ternary(); Eat(')');
                    var idx = double.IsNaN(iv) ? 0 : (int)iv;
                    var ch = idx >= 0 && idx < str.Length ? str[idx].ToString() : "";
                    return double.TryParse(ch, NumberStyles.Any, CultureInfo.InvariantCulture, out var cv) ? cv : double.NaN;
                }
                if (Str(".length"))
                {
                    _pos += ".length".Length;
                    return str.Length;
                }
                // 纯字符串当数值
                return double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var pv) ? pv : double.NaN;
            }

            // 数字字面量（含 .5 这种以点开头的写法）
            var ns2 = _pos;
            while (_pos < _s.Length && (char.IsDigit(_s[_pos]) || _s[_pos] == '.')) _pos++;
            if (_pos > ns2 && double.TryParse(_s.Substring(ns2, _pos - ns2), NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
                return num;

            // 未知字符，跳过避免死循环
            if (_pos < _s.Length) _pos++;
            return 0;
        }
    }
}
