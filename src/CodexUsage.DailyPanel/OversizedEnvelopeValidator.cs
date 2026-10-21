using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace CodexUsage.DailyPanel;

/// <summary>
/// Bounded, streaming JSON grammar validator used only for rows above the normal parser limit.
/// It stores no body, caps nesting at 64 and root keys at 128 names of 256 UTF-16 units.
/// A row is safe to discard only with a unique exact root type from the nonstatistics allowlist.
/// Unknown or over-limit envelopes are deliberately left untrusted, even if their JSON is valid.
/// </summary>
internal sealed class OversizedEnvelopeValidator
{
    private enum Scope : byte { ObjectFirst, ObjectName, Colon, ObjectValue, ObjectAfter, ArrayFirst, ArrayValue, ArrayAfter }
    private enum Token : byte { None, String, Number, Literal }
    private enum Number : byte { Sign, Zero, Integer, Dot, Fraction, Exponent, ExponentSign, ExponentDigits }
    private readonly Scope[] _stack = new Scope[64];
    private readonly HashSet<string> _rootNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _payloadNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _rootMetadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _payloadMetadata = new(StringComparer.Ordinal);
    private string? _rootName, _payloadName;
    private bool _payloadObject, _insidePayload, _payloadUnique = true;
    private byte _numberSchemaLocation;
    private bool _numberSchemaOne;
    private int _depth;
    private bool _valid = true, _safe = true, _rootStarted, _rootObject, _rootTypeProperty;
    private string? _rootType;
    private Token _token;
    private Number _number;
    private string? _literal;
    private int _literalIndex;
    private StringBuilder? _text;
    private int _escape, _unicode, _continuations, _codepoint;
    private bool _highSurrogate;
    private byte _continuationMinimum = 0x80, _continuationMaximum = 0xbf;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (!_valid) return;
            Consume(value);
        }
    }

    public bool IsKnownNonStatistics()
    {
        if (_token == Token.Number) FinishNumber();
        return _valid && _safe && _token == Token.None && _rootStarted && _rootObject && _depth == 0
            && _rootType is "response_item" or "turn_context" or "compacted";
    }

    /// <summary>Returns a tiny metadata-only envelope after the entire original line has validated.
    /// Arguments, output and ordinary text are never retained or returned.</summary>
    public string? ToolMetadataLine()
    {
        if (!IsKnownNonStatistics() || _rootType != "response_item" || !_payloadObject || !_payloadUnique) return null;
        if (!_payloadMetadata.TryGetValue("type", out string? type)
            || type is not ("function_call" or "function_call_output" or "custom_tool_call" or "custom_tool_call_output")) return null;
        var envelope = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "response_item", ["payload"] = _payloadMetadata };
        foreach (var pair in _rootMetadata) envelope[pair.Key] = pair.Value;
        return JsonSerializer.Serialize(envelope);
    }

    public bool ToolTimingUnavailable()
    {
        if (!IsKnownNonStatistics() || _rootType != "response_item") return !IsKnownNonStatistics();
        if (!SupportedVersion(_rootMetadata) || !SupportedVersion(_payloadMetadata)) return true;
        if (ToolMetadataLine() is not null) return false;
        return !_payloadObject || !_payloadUnique || !_payloadMetadata.TryGetValue("type", out string? type)
            || type is not ("message" or "agent_message" or "additional_tools" or "reasoning" or "compaction" or "compaction_summary");
    }

    private static bool SupportedVersion(Dictionary<string, string?> metadata) =>
        !metadata.TryGetValue("schema_version", out var version) || version == "1";

    private void Consume(byte value)
    {
        if (_token == Token.String) { StringByte(value); return; }
        if (_token == Token.Literal)
        {
            if (value != _literal![_literalIndex++]) { _valid = false; return; }
            if (_literalIndex == _literal.Length) _token = Token.None;
            return;
        }
        if (_token == Token.Number)
        {
            if (NumberByte(value)) return;
            FinishNumber();
            if (!_valid) return;
        }
        if (value is (byte)' ' or (byte)'\t' or (byte)'\r') return;
        switch (value)
        {
            case (byte)'"':
                _token = Token.String;
                _text = new StringBuilder(32);
                _escape = _unicode = _continuations = 0;
                _highSurrogate = false;
                break;
            case (byte)'{':
                bool isRoot = !_rootStarted;
                bool isPayload = _depth == 1 && _rootName == "payload" && _stack[0] == Scope.ObjectValue;
                if (Value() && Push(Scope.ObjectFirst))
                {
                    if (isRoot) _rootObject = true;
                    if (isPayload) _payloadObject = _insidePayload = true;
                }
                break;
            case (byte)'[': if (Value()) Push(Scope.ArrayFirst); break;
            case (byte)'}':
                if (_depth == 0 || _stack[_depth - 1] is not (Scope.ObjectFirst or Scope.ObjectAfter)) _valid = false;
                else { if (_depth == 2) _insidePayload = false; _depth--; }
                break;
            case (byte)']':
                if (_depth == 0 || _stack[_depth - 1] is not (Scope.ArrayFirst or Scope.ArrayAfter)) _valid = false;
                else _depth--;
                break;
            case (byte)':':
                if (_depth == 0 || _stack[_depth - 1] != Scope.Colon) _valid = false;
                else _stack[_depth - 1] = Scope.ObjectValue;
                break;
            case (byte)',':
                if (_depth == 0) _valid = false;
                else if (_stack[_depth - 1] == Scope.ObjectAfter) _stack[_depth - 1] = Scope.ObjectName;
                else if (_stack[_depth - 1] == Scope.ArrayAfter) _stack[_depth - 1] = Scope.ArrayValue;
                else _valid = false;
                break;
            case (byte)'t': Literal("true"); break;
            case (byte)'f': Literal("false"); break;
            case (byte)'n': Literal("null"); break;
            default:
                if (value == '-' || value is >= (byte)'0' and <= (byte)'9')
                {
                    _numberSchemaLocation = (byte)(_depth == 1 && _rootName == "schema_version" ? 1
                        : _depth == 2 && _insidePayload && _payloadName == "schema_version" ? 2 : 0);
                    _numberSchemaOne = value == '1';
                    if (!Value()) return;
                    _token = Token.Number;
                    _number = value == '-' ? Number.Sign : value == '0' ? Number.Zero : Number.Integer;
                }
                else _valid = false;
                break;
        }
    }

    private bool Value(string? text = null)
    {
        if (_depth == 0)
        {
            if (_rootStarted) return _valid = false;
            _rootStarted = true;
            return true;
        }
        var scope = _stack[_depth - 1];
        if (scope == Scope.ObjectValue)
        {
            if (_depth == 1 && _rootTypeProperty) _rootType = text;
            if (_depth == 1 && _rootName is "timestamp" or "schema_version" or "turn_id") _rootMetadata[_rootName] = text;
            if (_depth == 2 && _insidePayload && _payloadName is "type" or "call_id" or "turn_id" or "schema_version") _payloadMetadata[_payloadName] = text;
            _stack[_depth - 1] = Scope.ObjectAfter;
        }
        else if (scope is Scope.ArrayFirst or Scope.ArrayValue) _stack[_depth - 1] = Scope.ArrayAfter;
        else return _valid = false;
        return true;
    }

    private bool Push(Scope scope)
    {
        if (_depth == _stack.Length) return _valid = false;
        _stack[_depth++] = scope;
        return true;
    }

    private void Literal(string expected)
    {
        if (!Value()) return;
        _token = Token.Literal;
        _literal = expected;
        _literalIndex = 1;
    }

    private bool NumberByte(byte value)
    {
        if (value is (byte)'-' or (byte)'+' or (byte)'.' or (byte)'e' or (byte)'E' or >= (byte)'0' and <= (byte)'9') _numberSchemaOne = false;
        bool digit = value is >= (byte)'0' and <= (byte)'9';
        switch (_number)
        {
            case Number.Sign:
                if (digit) { _number = value == '0' ? Number.Zero : Number.Integer; return true; }
                break;
            case Number.Zero:
                if (digit) { _valid = false; return true; }
                goto case Number.Integer;
            case Number.Integer:
                if (digit) return true;
                if (value == '.') { _number = Number.Dot; return true; }
                if (value is (byte)'e' or (byte)'E') { _number = Number.Exponent; return true; }
                break;
            case Number.Dot:
                if (digit) { _number = Number.Fraction; return true; }
                break;
            case Number.Fraction:
                if (digit) return true;
                if (value is (byte)'e' or (byte)'E') { _number = Number.Exponent; return true; }
                break;
            case Number.Exponent:
                if (value is (byte)'+' or (byte)'-') { _number = Number.ExponentSign; return true; }
                goto case Number.ExponentSign;
            case Number.ExponentSign:
                if (digit) { _number = Number.ExponentDigits; return true; }
                break;
            case Number.ExponentDigits: if (digit) return true; break;
        }
        return false;
    }

    private void FinishNumber()
    {
        _valid &= _number is Number.Zero or Number.Integer or Number.Fraction or Number.ExponentDigits;
        if (_numberSchemaLocation != 0)
        {
            var metadata = _numberSchemaLocation == 1 ? _rootMetadata : _payloadMetadata;
            metadata["schema_version"] = _numberSchemaOne ? "1" : null;
            _numberSchemaLocation = 0;
        }
        _token = Token.None;
    }

    private void StringByte(byte value)
    {
        if (_continuations != 0)
        {
            if (value < _continuationMinimum || value > _continuationMaximum) { _valid = false; return; }
            _continuationMinimum = 0x80; _continuationMaximum = 0xbf;
            _codepoint = (_codepoint << 6) | (value & 0x3f);
            if (--_continuations == 0)
            {
                if (_codepoint <= 0xffff) Capture((char)_codepoint);
                else { int scalar = _codepoint - 0x10000; Capture((char)(0xd800 + (scalar >> 10))); Capture((char)(0xdc00 + (scalar & 0x3ff))); }
            }
            return;
        }
        if (_escape >= 2)
        {
            int hex = value is >= (byte)'0' and <= (byte)'9' ? value - '0'
                : value is >= (byte)'a' and <= (byte)'f' ? value - 'a' + 10
                : value is >= (byte)'A' and <= (byte)'F' ? value - 'A' + 10 : -1;
            if (hex < 0) { _valid = false; return; }
            _unicode = (_unicode << 4) | hex;
            if (++_escape == 6)
            {
                // Reject unpaired escaped surrogates conservatively. Their decoded property
                // names are not dependable evidence for an unambiguous ordinary envelope.
                char decoded = (char)_unicode;
                if (char.IsHighSurrogate(decoded))
                {
                    if (_highSurrogate) _valid = false;
                    _highSurrogate = true;
                }
                else if (char.IsLowSurrogate(decoded))
                {
                    if (!_highSurrogate) _valid = false;
                    _highSurrogate = false;
                }
                else if (_highSurrogate) _valid = false;
                Capture(decoded); _escape = 0;
            }
            return;
        }
        if (_escape == 1)
        {
            _escape = 0;
            if (_highSurrogate && value != 'u') { _valid = false; return; }
            switch (value)
            {
                case (byte)'"': case (byte)'\\': case (byte)'/': Capture((char)value); break;
                case (byte)'b': Capture('\b'); break;
                case (byte)'f': Capture('\f'); break;
                case (byte)'n': Capture('\n'); break;
                case (byte)'r': Capture('\r'); break;
                case (byte)'t': Capture('\t'); break;
                case (byte)'u': _escape = 2; _unicode = 0; break;
                default: _valid = false; break;
            }
            return;
        }
        if (_highSurrogate && value != '\\') { _valid = false; return; }
        if (value == '"')
        {
            _token = Token.None;
            string? text = _text?.ToString();
            _text = null;
            if (_depth > 0 && _stack[_depth - 1] is Scope.ObjectFirst or Scope.ObjectName)
            {
                if (_depth == 1)
                {
                    if (text is null || _rootNames.Count >= 128 || !_rootNames.Add(text)) _safe = false;
                    _rootTypeProperty = text == "type";
                    _rootName = text;
                }
                else if (_depth == 2 && _insidePayload)
                {
                    if (text is null || _payloadNames.Count >= 128 || !_payloadNames.Add(text)) _payloadUnique = false;
                    _payloadName = text;
                }
                _stack[_depth - 1] = Scope.Colon;
            }
            else Value(text);
            return;
        }
        if (value == '\\') { _escape = 1; return; }
        if (value < 0x20) { _valid = false; return; }
        if (value < 0x80) { Capture((char)value); return; }
        _continuationMinimum = 0x80; _continuationMaximum = 0xbf;
        if (value is >= 0xc2 and <= 0xdf) { _continuations = 1; _codepoint = value & 0x1f; }
        else if (value is >= 0xe0 and <= 0xef)
        {
            _continuations = 2; _codepoint = value & 0x0f;
            if (value == 0xe0) _continuationMinimum = 0xa0;
            if (value == 0xed) _continuationMaximum = 0x9f;
        }
        else if (value is >= 0xf0 and <= 0xf4)
        {
            _continuations = 3; _codepoint = value & 7;
            if (value == 0xf0) _continuationMinimum = 0x90;
            if (value == 0xf4) _continuationMaximum = 0x8f;
        }
        else _valid = false;
    }

    private void Capture(char value)
    {
        if (_text is not null && _text.Length < 256) _text.Append(value);
        else _text = null;
    }
}
