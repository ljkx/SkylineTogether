using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SkylineTogether.Core.Json
{
    /// <summary>
    /// Forward-only JSON writer (diagnostics dumps, reports, printing parsed values). It validates the structure as it
    /// goes and throws <see cref="InvalidOperationException"/> on misuse: a name outside an object, a value without a
    /// name inside an object, unbalanced ends, or a second root value. Strings are escaped (quotes, backslashes, control
    /// characters, U+2028/U+2029 and unpaired surrogates as <c>\uXXXX</c>). Non-finite doubles, which JSON cannot
    /// express, are written as the strings <c>"NaN"</c>, <c>"Infinity"</c> and <c>"-Infinity"</c>. With
    /// <c>indent</c>, nested values go on their own lines, indented by two spaces. Not thread-safe.
    /// </summary>
    public sealed class JsonWriter
    {
        private enum Container : byte { Object, Array }

        private struct Frame
        {
            public Container Kind;
            public int Count;
            public bool NamePending;
        }

        private readonly TextWriter _out;
        private readonly bool _indent;
        private readonly List<Frame> _stack = new List<Frame>();
        private bool _rootWritten;

        public JsonWriter(TextWriter output, bool indent)
        {
            _out = output ?? throw new ArgumentNullException(nameof(output));
            _indent = indent;
        }

        /// <summary>True when one complete root value has been written.</summary>
        public bool IsComplete => _rootWritten && _stack.Count == 0;

        public void BeginObject()
        {
            BeforeValue();
            _out.Write('{');
            _stack.Add(new Frame { Kind = Container.Object });
        }

        public void EndObject() => End(Container.Object, '}');

        public void BeginArray()
        {
            BeforeValue();
            _out.Write('[');
            _stack.Add(new Frame { Kind = Container.Array });
        }

        public void EndArray() => End(Container.Array, ']');

        /// <summary>The name of the next member of the current object.</summary>
        public void Name(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (_stack.Count == 0 || _stack[_stack.Count - 1].Kind != Container.Object)
            {
                throw new InvalidOperationException("JsonWriter: a name is only valid inside an object");
            }
            Frame frame = _stack[_stack.Count - 1];
            if (frame.NamePending) throw new InvalidOperationException("JsonWriter: two names without a value in between");
            if (frame.Count > 0) _out.Write(',');
            NewLine(_stack.Count);
            WriteString(name);
            _out.Write(_indent ? ": " : ":");
            frame.NamePending = true;
            _stack[_stack.Count - 1] = frame;
        }

        /// <summary>A string value (null writes <c>null</c>).</summary>
        public void Value(string v)
        {
            BeforeValue();
            if (v == null) _out.Write("null");
            else WriteString(v);
        }

        public void Value(double v)
        {
            BeforeValue();
            if (double.IsNaN(v)) WriteString("NaN");
            else if (double.IsPositiveInfinity(v)) WriteString("Infinity");
            else if (double.IsNegativeInfinity(v)) WriteString("-Infinity");
            else _out.Write(JsonNumber.FormatLexeme(v));
        }

        public void Value(long v)
        {
            BeforeValue();
            _out.Write(v.ToString(CultureInfo.InvariantCulture));
        }

        public void Value(bool v)
        {
            BeforeValue();
            _out.Write(v ? "true" : "false");
        }

        public void Null()
        {
            BeforeValue();
            _out.Write("null");
        }

        /// <summary>A number exactly as parsed (keeps the original text when re-printing a parsed document).</summary>
        internal void RawNumber(string lexeme)
        {
            BeforeValue();
            _out.Write(lexeme);
        }

        private void BeforeValue()
        {
            if (_stack.Count == 0)
            {
                if (_rootWritten) throw new InvalidOperationException("JsonWriter: the document already has a root value");
                _rootWritten = true;
                return;
            }
            Frame frame = _stack[_stack.Count - 1];
            if (frame.Kind == Container.Object)
            {
                if (!frame.NamePending) throw new InvalidOperationException("JsonWriter: a value inside an object needs a name first");
                frame.NamePending = false;
            }
            else
            {
                if (frame.Count > 0) _out.Write(',');
                NewLine(_stack.Count);
            }
            frame.Count++;
            _stack[_stack.Count - 1] = frame;
        }

        private void End(Container kind, char closing)
        {
            if (_stack.Count == 0 || _stack[_stack.Count - 1].Kind != kind)
            {
                throw new InvalidOperationException("JsonWriter: unbalanced End" + kind);
            }
            Frame frame = _stack[_stack.Count - 1];
            if (frame.NamePending) throw new InvalidOperationException("JsonWriter: a name without a value");
            _stack.RemoveAt(_stack.Count - 1);
            if (frame.Count > 0) NewLine(_stack.Count);
            _out.Write(closing);
        }

        private void NewLine(int depth)
        {
            if (!_indent) return;
            _out.Write('\n');
            for (int i = 0; i < depth; i++) _out.Write("  ");
        }

        private void WriteString(string s)
        {
            _out.Write('"');
            int runStart = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                string escape = null;
                switch (c)
                {
                    case '"': escape = "\\\""; break;
                    case '\\': escape = "\\\\"; break;
                    case '\b': escape = "\\b"; break;
                    case '\f': escape = "\\f"; break;
                    case '\n': escape = "\\n"; break;
                    case '\r': escape = "\\r"; break;
                    case '\t': escape = "\\t"; break;
                    default:
                        if (c < 0x20 || c == LineSeparator || c == ParagraphSeparator) escape = UnicodeEscape(c);
                        else if (char.IsHighSurrogate(c))
                        {
                            if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;   // a valid pair stays raw
                            else escape = UnicodeEscape(c);
                        }
                        else if (char.IsLowSurrogate(c)) escape = UnicodeEscape(c);
                        break;
                }
                if (escape == null) continue;
                if (i > runStart) _out.Write(s.Substring(runStart, i - runStart));
                _out.Write(escape);
                runStart = i + 1;
            }
            if (runStart < s.Length) _out.Write(runStart == 0 ? s : s.Substring(runStart));
            _out.Write('"');
        }

        private static string UnicodeEscape(char c) => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture);

        private const char LineSeparator = (char)0x2028;
        private const char ParagraphSeparator = (char)0x2029;
    }
}
