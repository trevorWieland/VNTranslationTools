﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VNTextPatch.Shared.Util;

namespace VNTextPatch.Shared.Scripts.Yuris
{
    public class YurisScript : IScript
    {
        private static class ExpressionOpcodes
        {
            public const byte PerformVarIndexation = 0x29;
            public const byte Add = 0x2B;
            public const byte PushString = 0x4D;
            public const byte PrepareVarIndexation = 0x56;
            public const byte PushInt16 = 0x57;
        }

        private string _lastFolderPath;
        private YurisCommandList _commandList;
        private byte _wordCommandId;
        private byte _evalCommandId;
        private byte _gosubCommandId;
        private byte _cgCommandId;

        private byte[] _script;
        private uint _key;

        private int _instructionsOffset;
        private int _instructionsSize;

        private int _attributeDescriptorsOffset;
        private int _attributeDescriptorsSize;

        private int _attributeValuesOffset;
        private int _attributeValuesSize;

        private int _lineNumbersOffset;
        private int _lineNumbersSize;

        public string Extension => ".ybn";

        public void Load(ScriptLocation location)
        {
            LoadCommandListForScript(location);
            _script = File.ReadAllBytes(location.ToFilePath());

            MemoryStream stream = new MemoryStream(_script);
            BinaryReader reader = new BinaryReader(stream);
            int magic = reader.ReadInt32();
            if (magic != 0x42545359)
            {
                _script = null;
                return;
            }

            int version = reader.ReadInt32();
            int numInstructions = reader.ReadInt32();
            _instructionsSize = reader.ReadInt32();
            if (_instructionsSize != numInstructions * 4)
                throw new InvalidDataException();

            _attributeDescriptorsSize = reader.ReadInt32();
            _attributeValuesSize = reader.ReadInt32();
            _lineNumbersSize = reader.ReadInt32();
            reader.ReadInt32();

            _instructionsOffset = (int)stream.Position;
            _attributeDescriptorsOffset = _instructionsOffset + _instructionsSize;
            _attributeValuesOffset = _attributeDescriptorsOffset + _attributeDescriptorsSize;
            _lineNumbersOffset = _attributeValuesOffset + _attributeValuesSize;
            if (_lineNumbersOffset + _lineNumbersSize != _script.Length)
                throw new InvalidDataException();

            if (_attributeDescriptorsSize > 0)
                _key = BitConverter.ToUInt32(_script, _attributeDescriptorsOffset + 8);

            ToggleScriptEncryption(_script, _key);
        }

        public IEnumerable<ScriptString> GetStrings()
        {
            if (_script == null)
                yield break;

            foreach (List<YurisAttribute> attrs in GetStringAttributeGroups())
            {
                string str = string.Join("", attrs.Select(a => GetAttributeValue(a)));
                Match dialogueMatch = Regex.Match(str, @"^(?<name>.+?)(?:[「『](?<text>.+)[』」]|(?<text>（.+）))$", RegexOptions.Singleline);
                if (dialogueMatch.Success)
                {
                    yield return new ScriptString(dialogueMatch.Groups["name"].Value, ScriptStringType.CharacterName);
                    yield return new ScriptString(dialogueMatch.Groups["text"].Value, ScriptStringType.Message);
                }
                else
                {
                    yield return new ScriptString(str, ScriptStringType.Message);
                }
            }
        }

        public void WritePatched(IEnumerable<ScriptString> strings, ScriptLocation location)
        {
            using (MemoryStream inputStream = new MemoryStream(_script))
            using (MemoryStream outputStream = new MemoryStream())
            using (IEnumerator<ScriptString> stringEnumerator = strings.GetEnumerator())
            {
                BinaryPatcher patcher = new BinaryPatcher(inputStream, outputStream, a => _attributeValuesOffset + a, o => o - _attributeValuesOffset);
                foreach (List<YurisAttribute> attrs in GetStringAttributeGroups())
                {
                    for (int i = 0; i < attrs.Count; i++)
                    {
                        patcher.CopyUpTo(attrs[i].ValueOffset);

                        byte[] newData;
                        if (i == 0)
                        {
                            string text = GetNextMessage(stringEnumerator);
                            newData = SerializeAttributeValue(attrs[i], text);
                        }
                        else
                        {
                            newData = Array.Empty<byte>();
                        }

                        patcher.PatchInt32(attrs[i].DescriptorOffset + 4, newData.Length);
                        patcher.ReplaceBytes(attrs[i].ValueLength, newData);
                    }
                }

                if (stringEnumerator.MoveNext())
                    throw new InvalidDataException("Too many lines in translated script");

                patcher.CopyUpTo(_lineNumbersOffset);

                foreach (YurisAttribute attr in GetAttributes())
                {
                    patcher.PatchAddress(attr.DescriptorOffset + 8);
                }
                
                int newAttributeValuesSize = patcher.CurrentOutputPosition - _attributeValuesOffset;
                patcher.PatchInt32(0x14, newAttributeValuesSize);

                patcher.CopyUpTo(_script.Length);

                outputStream.TryGetBuffer(out ArraySegment<byte> newScript);
                ToggleScriptEncryption(newScript.Array, _key);
                using (Stream outputFileStream = File.Open(location.ToFilePath(), FileMode.Create, FileAccess.Write))
                {
                    outputFileStream.Write(newScript.Array, 0, newScript.Count);
                }
            }
        }

        private static string GetNextMessage(IEnumerator<ScriptString> stringEnumerator)
        {
            if (!stringEnumerator.MoveNext())
                throw new InvalidDataException("Not enough lines in translated script");

            if (stringEnumerator.Current.Type == ScriptStringType.CharacterName)
            {
                string name = stringEnumerator.Current.Text;
                if (!stringEnumerator.MoveNext())
                    throw new InvalidDataException("Not enough lines in translated script");

                //return $"{name}「{stringEnumerator.Current.Text}」";
                return $"{name}「{MonospaceWordWrapper.Default.Wrap(stringEnumerator.Current.Text)}」";
            }
            else
            {
                //return stringEnumerator.Current.Text;
                return MonospaceWordWrapper.Default.Wrap(stringEnumerator.Current.Text);
            }
        }

        private IEnumerable<List<YurisAttribute>> GetStringAttributeGroups()
        {
            using IEnumerator<YurisAttribute> attrEnumerator = GetAttributes().GetEnumerator();
            List<YurisAttribute> commandAttrs = new List<YurisAttribute>();
            List<YurisAttribute> stringAttrs = new List<YurisAttribute>();
            foreach (YurisCommand command in GetCommands())
            {
                commandAttrs.Clear();
                for (int i = 0; i < command.NumAttributes; i++)
                {
                    if (!attrEnumerator.MoveNext())
                        throw new InvalidDataException();

                    commandAttrs.Add(attrEnumerator.Current);
                }

                IEnumerable<YurisAttribute> commandStringAttrs = GetStringAttributes(command, commandAttrs);
                if (command.Id == _gosubCommandId)
                {
                    if (stringAttrs.Count > 0)
                    {
                        yield return stringAttrs;
                        stringAttrs.Clear();
                    }
                    foreach (YurisAttribute stringAttr in commandStringAttrs)
                    {
                        yield return new List<YurisAttribute> { stringAttr };
                    }
                    continue;
                }

                stringAttrs.AddRange(commandStringAttrs);

                if (command.Id != _wordCommandId && command.Id != _evalCommandId && stringAttrs.Count > 0)
                {
                    yield return stringAttrs;
                    stringAttrs.Clear();
                }
            }
        }

        private IEnumerable<YurisAttribute> GetStringAttributes(YurisCommand command, List<YurisAttribute> attrs)
        {
            if (command.Id == _wordCommandId ||
                command.Id == _evalCommandId)
            {
                yield return attrs[0];
            }
            else if (command.Id == _gosubCommandId)
            {
                string subName = GetAttributeValue(attrs[0]);
                if (subName == null ||
                    !subName.Equals("ES.CHAR.NAME", StringComparison.InvariantCultureIgnoreCase) &&
                    !subName.Equals("ES.SEL.SET", StringComparison.InvariantCultureIgnoreCase))
                {
                    yield break;
                }

                for (int i = 1; i < attrs.Count; i++)
                {
                    if (string.IsNullOrEmpty(GetAttributeValue(attrs[i])))
                        break;

                    yield return attrs[i];
                }
            }
            else if (command.Id != _cgCommandId)
            {
                foreach (YurisAttribute attr in attrs.Where(a => a.Type == YurisAttributeType.Expression))
                {
                    string value = GetAttributeValue(attr);
                    if (value != null && StringUtil.ContainsJapaneseText(value))
                        yield return attr;
                }
            }
        }

        private IEnumerable<YurisCommand> GetCommands()
        {
            MemoryStream stream = new MemoryStream(_script);
            BinaryReader reader = new BinaryReader(stream);
            YurisCommand command = new YurisCommand();
            for (stream.Position = _instructionsOffset; stream.Position < _attributeDescriptorsOffset;)
            {
                command.Read(reader);
                yield return command;
            }
        }

        private IEnumerable<YurisAttribute> GetAttributes()
        {
            MemoryStream stream = new MemoryStream(_script);
            BinaryReader reader = new BinaryReader(stream);
            YurisAttribute attr = new YurisAttribute();
            for (stream.Position = _attributeDescriptorsOffset; stream.Position < _attributeValuesOffset; )
            {
                attr.Read(reader, _attributeValuesOffset);
                yield return attr;
            }
        }

        private string GetAttributeValue(in YurisAttribute attr)
        {
            switch (attr.Type)
            {
                case YurisAttributeType.Raw:
                {
                    YurisControlCodesToStandard(_script, attr.ValueOffset, attr.ValueLength);
                    return StringUtil.SjisEncoding.GetString(_script, attr.ValueOffset, attr.ValueLength);
                }

                case YurisAttributeType.Expression:
                {
                    return EvaluatePushStringExpression(attr) ??
                           EvaluateChrExpression(attr);
                }

                default:
                    return null;
            }
        }

        private string EvaluatePushStringExpression(in YurisAttribute attr)
        {
            // We expect expression bytecode of the form:
            // 4D           pushstring opcode
            // XX XX        argument length
            // 22 ... 22    quoted string (could also be 27 ... 27)
            if (attr.ValueLength < 3)
                return null;

            byte opcode = _script[attr.ValueOffset];
            if (opcode != ExpressionOpcodes.PushString)
                return null;

            int argLength = BitConverter.ToUInt16(_script, attr.ValueOffset + 1);
            if (3 + argLength != attr.ValueLength)
                return null;

            string str = StringUtil.SjisEncoding.GetString(_script, attr.ValueOffset + 3, argLength);
            str = UnquoteString(str);
            str = Regex.Replace(str, @"(?<!\r)\n", "\r\n");
            return str;
        }

        private string EvaluateChrExpression(in YurisAttribute attr)
        {
            // Currently only supporting control codes
            if (attr.ValueLength != 33)
                return null;

            // Expecting the following bytecode:
            // 56 03 00 24 XX XX        preparevarindexation $_CHR
            // 57 02 00 EF 00           pushint16 0xEF
            // 29 01 00 00              performvarindexation
            // 56 03 00 24 XX XX        preparevarindexation $_CHR
            // 57 02 00 XX XX           pushint16 XXXX
            // 29 01 00 00              performvarindexation
            // 2B 00 00                 add
            if (_script[attr.ValueOffset + 0] != ExpressionOpcodes.PrepareVarIndexation ||
                _script[attr.ValueOffset + 6] != ExpressionOpcodes.PushInt16 ||
                _script[attr.ValueOffset + 9] != 0xEF ||
                _script[attr.ValueOffset + 10] != 0x00 ||
                _script[attr.ValueOffset + 11] != ExpressionOpcodes.PerformVarIndexation ||
                _script[attr.ValueOffset + 15] != ExpressionOpcodes.PrepareVarIndexation ||
                _script[attr.ValueOffset + 19] != _script[attr.ValueOffset + 4] ||
                _script[attr.ValueOffset + 20] != _script[attr.ValueOffset + 5] ||
                _script[attr.ValueOffset + 21] != ExpressionOpcodes.PushInt16 ||
                _script[attr.ValueOffset + 26] != ExpressionOpcodes.PerformVarIndexation ||
                _script[attr.ValueOffset + 30] != ExpressionOpcodes.Add)
            {
                return null;
            }

            byte[] controlCode = { 0xEF, _script[attr.ValueOffset + 24] };
            YurisControlCodesToStandard(controlCode, 0, 2);
            return StringUtil.SjisEncoding.GetString(controlCode);
        }

        private static byte[] SerializeAttributeValue(in YurisAttribute attr, string value)
        {
            switch (attr.Type)
            {
                case YurisAttributeType.Raw:
                {
                    byte[] data = StringUtil.SjisTunnelEncoding.GetBytes(value);
                    StandardControlCodesToYuris(data, 0, data.Length);
                    return data;
                }

                case YurisAttributeType.Expression:
                {
                    value = value.Replace("\r", "");
                    value = QuoteString(value);
                    
                    int numBytes = StringUtil.SjisTunnelEncoding.GetByteCount(value);
                    byte[] data = new byte[3 + numBytes];
                    data[0] = 0x4D;
                    data[1] = (byte)numBytes;
                    data[2] = (byte)(numBytes >> 8);
                    StringUtil.SjisTunnelEncoding.GetBytes(value, 0, value.Length, data, 3);
                    return data;
                }

                default:
                    throw new NotSupportedException();
            }
        }

        private static string QuoteString(string str)
        {
            // In YU-RIS, strings can normally only be delimited with double or single quotes.
            // However, because the engine doesn't have \" or \' escape sequences,
            // it's not possible to have a string containing both types of quotes -
            // you can only do "'" or '"'. The workaround is to use an entirely different
            // character like ` in the compiled script. This works because the runtime only
            // checks that the first and last character are the same, not what character it is.
            if (str.Contains("`"))
                throw new InvalidDataException($"Message can't contain backticks [{str}]");

            str = StringUtil.EscapeC(str);
            return $"`{str}`";
        }

        private static string UnquoteString(string str)
        {
            str = str.Substring(1, str.Length - 2);
            return StringUtil.UnescapeC(str);
        }

        private static void YurisControlCodesToStandard(byte[] str, int offset, int length)
        {
            for (int i = offset; i < offset + length - 1; )
            {
                if (str[i] != 0xEF)
                {
                    i += StringUtil.IsShiftJisLeadByte(str[i]) ? 2 : 1;
                    continue;
                }

                switch (str[i + 1])
                {
                    case 0xF0:
                        // Line break
                        str[i] = (byte)'\r';
                        str[i + 1] = (byte)'\n';
                        break;

                    case 0xF2:
                        // Page break
                        str[i] = (byte)'\\';
                        str[i + 1] = (byte)'p';
                        break;

                    case 0xF3:
                        // Wait for click
                        str[i] = (byte)'\\';
                        str[i + 1] = (byte)'c';
                        break;
                }
                i += 2;
            }
        }

        private static void StandardControlCodesToYuris(byte[] str, int offset, int length)
        {
            for (int i = offset; i < offset + length - 1; )
            {
                if (str[i] == '\r' && str[i + 1] == '\n')
                {
                    // Line break
                    str[i] = 0xEF;
                    str[i + 1] = 0xF0;
                    i += 2;
                }
                else if (str[i] == '\\' && str[i + 1] == 'p')
                {
                    // Page break
                    str[i] = 0xEF;
                    str[i + 1] = 0xF2;
                    i += 2;
                }
                else if (str[i] == '\\' && str[i + 1] == 'c')
                {
                    // Wait for click
                    str[i] = 0xEF;
                    str[i + 1] = 0xF3;
                    i += 2;
                }
                else
                {
                    i += StringUtil.IsShiftJisLeadByte(str[i]) ? 2 : 1;
                }
            }
        }

        private void LoadCommandListForScript(ScriptLocation location)
        {
            string folderPath = ((FolderScriptCollection)location.Collection).FolderPath;
            if (folderPath == _lastFolderPath)
                return;

            string filePath = Path.Combine(folderPath, "ysc.ybn");
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Could not find {filePath}");

            _commandList = new YurisCommandList(filePath);
            _wordCommandId = _commandList.GetCommandId("WORD");
            _evalCommandId = _commandList.GetCommandId("_");
            _gosubCommandId = _commandList.GetCommandId("GOSUB");
            _cgCommandId = _commandList.GetCommandId("CG");
            _lastFolderPath = folderPath;
        }

        private static void ToggleScriptEncryption(byte[] script, uint key)
        {
            if (key == 0)
                return;

            int dataOffset = 0x20;
            for (int sizeOffset = 0xC; sizeOffset < 0x1C; sizeOffset += 4)
            {
                int size = BitConverter.ToInt32(script, sizeOffset);
                BinaryUtil.Xor(script, dataOffset, size, key);
                dataOffset += size;
            }
        }
    }
}
