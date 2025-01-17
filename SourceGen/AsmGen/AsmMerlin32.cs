﻿/*
 * Copyright 2019 faddenSoft
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

using Asm65;
using CommonUtil;

namespace SourceGen.AsmGen {
    #region IGenerator

    /// <summary>
    /// Generate source code compatible with Brutal Deluxe's Merlin 32 assembler
    /// (https://www.brutaldeluxe.fr/products/crossdevtools/merlin/).
    /// </summary>
    public class GenMerlin32 : IGenerator {
        private const string ASM_FILE_SUFFIX = "_merlin32.S";   // must start with underscore

        // IGenerator
        public DisasmProject Project { get; private set; }

        // IGenerator
        public Formatter SourceFormatter { get; private set; }

        // IGenerator
        public AppSettings Settings { get; private set; }

        // IGenerator
        public AssemblerQuirks Quirks { get; private set; }

        // IGenerator
        public LabelLocalizer Localizer { get { return mLocalizer; } }

        // IGenerator
        public int StartOffset { get { return 0; } }

        /// <summary>
        /// List of binary include sections found in the project.
        /// </summary>
        private List<BinaryInclude.Excision> mBinaryIncludes = new List<BinaryInclude.Excision>();

        /// <summary>
        /// Working directory, i.e. where we write our output file(s).
        /// </summary>
        private string mWorkDirectory;

        /// <summary>
        /// Influences whether labels are put on their own line.
        /// </summary>
        private GenCommon.LabelPlacement mLabelNewLine;

        /// <summary>
        /// Output column widths.
        /// </summary>
        private int[] mColumnWidths;

        /// <summary>
        /// Base filename.  Typically the project file name without the ".dis65" extension.
        /// </summary>
        private string mFileNameBase;

        /// <summary>
        /// StringBuilder to use when composing a line.  Held here to reduce allocations.
        /// </summary>
        private StringBuilder mLineBuilder = new StringBuilder(100);

        /// <summary>
        /// Label localization helper.
        /// </summary>
        private LabelLocalizer mLocalizer;

        /// <summary>
        /// Stream to send the output to.
        /// </summary>
        private StreamWriter mOutStream;

        /// <summary>
        /// Address of next byte of output.
        /// </summary>
        private int mNextAddress = -1;

        /// <summary>
        /// True if we've seen an "is relative" flag in a block of address region start directives.
        /// </summary>
        /// <remarks>
        /// The trick with IsRelative is that, if there are multiple arstarts at the same
        /// offset, we need to output some or all of them, starting from the one just before
        /// the first IsRelative start.  We probably want to disable the use of Flush and
        /// just generate them as they appear, using the next Flush as the signal to return
        /// to standard behavior.
        /// </remarks>
        bool mIsInRelative = false;

        /// <summary>
        /// Holds detected version of configured assembler.
        /// </summary>
        private CommonUtil.Version mAsmVersion = CommonUtil.Version.NO_VERSION;

        // Interesting versions.
        private static CommonUtil.Version V1_0 = new CommonUtil.Version(1, 0);


        // Pseudo-op string constants.
        private static PseudoOp.PseudoOpNames sDataOpNames =
            new PseudoOp.PseudoOpNames(new Dictionary<string, string> {
                { "EquDirective", "equ" },
                { "VarDirective", "equ" },
                { "ArStartDirective", "org" },
                { "ArEndDirective", "adrend" },     // on-screen display only
                //RegWidthDirective
                //DataBankDirective
                { "DefineData1", "dfb" },
                { "DefineData2", "dw" },
                { "DefineData3", "adr" },
                { "DefineData4", "adrl" },
                { "DefineBigData2", "ddb" },
                //DefineBigData3
                //DefineBigData4
                { "Fill", "ds" },
                { "Dense", "hex" },
                { "Uninit", "ds" },
                //Junk
                //Align
                { "BinaryInclude", "putbin" },
                { "StrGeneric", "asc" },
                { "StrReverse", "rev" },
                //StrNullTerm
                { "StrLen8", "str" },
                { "StrLen16", "strl" },
                { "StrDci", "dci" },
        });
        private const string REG_WIDTH_DIRECTIVE = "mx";


        // IGenerator
        public void GetDefaultDisplayFormat(out PseudoOp.PseudoOpNames pseudoOps,
                out Formatter.FormatConfig formatConfig) {
            pseudoOps = sDataOpNames;

            formatConfig = new Formatter.FormatConfig();
            SetFormatConfigValues(ref formatConfig);
        }

        // IGenerator
        public void Configure(DisasmProject project, string workDirectory, string fileNameBase,
                AssemblerVersion asmVersion, AppSettings settings) {
            Debug.Assert(project != null);
            Debug.Assert(!string.IsNullOrEmpty(workDirectory));
            Debug.Assert(!string.IsNullOrEmpty(fileNameBase));

            Project = project;
            Quirks = new AssemblerQuirks();
            if (asmVersion != null) {
                mAsmVersion = asmVersion.Version;       // Use the actual version.
            } else {
                mAsmVersion = V1_0;                     // No assembler installed, use default.
            }

            Quirks.NoPcRelBankWrap = true;
            Quirks.TracksSepRepNotEmu = true;

            mWorkDirectory = workDirectory;
            mFileNameBase = fileNameBase;
            Settings = settings;

            mLabelNewLine = Settings.GetEnum(AppSettings.SRCGEN_LABEL_NEW_LINE,
                    GenCommon.LabelPlacement.SplitIfTooLong);

            AssemblerConfig config = AssemblerConfig.GetConfig(settings,
                AssemblerInfo.Id.Merlin32);
            mColumnWidths = (int[])config.ColumnWidths.Clone();
        }

        /// <summary>
        /// Configures the assembler-specific format items.
        /// </summary>
        private void SetFormatConfigValues(ref Formatter.FormatConfig config) {
            config.OperandWrapLen = 64;
            config.ForceDirectOpcodeSuffix = string.Empty;
            config.ForceAbsOpcodeSuffix = ":";
            config.ForceLongOpcodeSuffix = "l";
            config.ForceDirectOperandPrefix = string.Empty;
            config.ForceAbsOperandPrefix = string.Empty;
            config.ForceLongOperandPrefix = string.Empty;
            config.LocalVariableLabelPrefix = "]";
            config.EndOfLineCommentDelimiter = ";";
            config.FullLineCommentDelimiterBase = "*";
            config.NonUniqueLabelPrefix = ":";
            config.CommaSeparatedDense = false;
            config.ExprMode = Formatter.FormatConfig.ExpressionMode.Merlin;

            Formatter.DelimiterSet charSet = new Formatter.DelimiterSet();
            charSet.Set(CharEncoding.Encoding.Ascii, Formatter.SINGLE_QUOTE_DELIM);
            charSet.Set(CharEncoding.Encoding.HighAscii, Formatter.DOUBLE_QUOTE_DELIM);
            config.CharDelimiters = charSet;
        }

        // IGenerator; executes on background thread
        public GenerationResults GenerateSource(BackgroundWorker worker) {
            List<string> pathNames = new List<string>(1);

            string fileName = mFileNameBase + ASM_FILE_SUFFIX;
            string pathName = Path.Combine(mWorkDirectory, fileName);
            pathNames.Add(pathName);

            Formatter.FormatConfig config = new Formatter.FormatConfig();
            GenCommon.ConfigureFormatterFromSettings(Settings, ref config);
            SetFormatConfigValues(ref config);
            SourceFormatter = new Formatter(config);

            string msg = string.Format(Res.Strings.PROGRESS_GENERATING_FMT, pathName);
            worker.ReportProgress(0, msg);

            mLocalizer = new LabelLocalizer(Project);
            mLocalizer.LocalPrefix = ":";
            // don't need to set QuirkNoOpcodeMnemonics
            mLocalizer.Analyze();

            // Use UTF-8 encoding, without a byte-order mark.
            using (StreamWriter sw = new StreamWriter(pathName, false, new UTF8Encoding(false))) {
                mOutStream = sw;

                if (Settings.GetBool(AppSettings.SRCGEN_ADD_IDENT_COMMENT, false)) {
                    // No version-specific stuff yet.  We're generating code for v1.0.
                    OutputLine(SourceFormatter.FullLineCommentDelimiterPlus +
                        string.Format(Res.Strings.GENERATED_FOR_VERSION_FMT,
                            "Merlin 32", mAsmVersion, string.Empty));
                }

                GenCommon.Generate(this, sw, worker);
            }
            mOutStream = null;

            return new GenerationResults(pathNames, string.Empty, mBinaryIncludes);
        }

        // IGenerator
        public void OutputDataOp(int offset) {
            Formatter formatter = SourceFormatter;
            byte[] data = Project.FileData;
            Anattrib attr = Project.GetAnattrib(offset);

            string labelStr = string.Empty;
            if (attr.Symbol != null) {
                labelStr = mLocalizer.ConvLabel(attr.Symbol.Label);
            }

            string commentStr = SourceFormatter.FormatEolComment(Project.Comments[offset]);
            string opcodeStr, operandStr;

            FormatDescriptor dfd = attr.DataDescriptor;
            Debug.Assert(dfd != null);
            int length = dfd.Length;
            Debug.Assert(length > 0);

            bool multiLine = false;
            switch (dfd.FormatType) {
                case FormatDescriptor.Type.Default:
                    if (length != 1) {
                        Debug.Assert(false);
                        length = 1;
                    }
                    opcodeStr = sDataOpNames.DefineData1;
                    int operand = RawData.GetWord(data, offset, length, false);
                    operandStr = formatter.FormatHexValue(operand, length * 2);
                    break;
                case FormatDescriptor.Type.NumericLE:
                    opcodeStr = sDataOpNames.GetDefineData(length);
                    operand = RawData.GetWord(data, offset, length, false);
                    if (length == 1 && dfd.IsStringOrCharacter &&
                            ((operand & 0x7f) == '{' || (operand & 0x7f) == '}')) {
                        // Merlin32 can't handle "DFB '{'", so just output hex.
                        operandStr = formatter.FormatHexValue(operand, length * 2);
                    } else {
                        operandStr = PseudoOp.FormatNumericOperand(formatter, Project.SymbolTable,
                            mLocalizer.LabelMap, dfd, operand, length,
                            PseudoOp.FormatNumericOpFlags.OmitLabelPrefixSuffix);
                    }
                    break;
                case FormatDescriptor.Type.NumericBE:
                    opcodeStr = sDataOpNames.GetDefineBigData(length);
                    if ((string.IsNullOrEmpty(opcodeStr))) {
                        // Nothing defined, output as comma-separated single-byte values.
                        GenerateShortSequence(offset, length, out opcodeStr, out operandStr);
                    } else {
                        operand = RawData.GetWord(data, offset, length, true);
                        operandStr = PseudoOp.FormatNumericOperand(formatter, Project.SymbolTable,
                            mLocalizer.LabelMap, dfd, operand, length,
                            PseudoOp.FormatNumericOpFlags.OmitLabelPrefixSuffix);
                    }
                    break;
                case FormatDescriptor.Type.Fill:
                    opcodeStr = sDataOpNames.Fill;
                    if (data[offset] == 0) {
                        operandStr = length.ToString();
                    } else {
                        operandStr = length + "," + formatter.FormatHexValue(data[offset], 2);
                    }
                    break;
                case FormatDescriptor.Type.Dense:
                    multiLine = true;
                    opcodeStr = operandStr = null;
                    OutputDenseHex(offset, length, labelStr, commentStr);
                    break;
                case FormatDescriptor.Type.Uninit:
                case FormatDescriptor.Type.Junk:
                    int fillVal = Helper.CheckRangeHoldsSingleValue(data, offset, length);
                    if (fillVal >= 0) {
                        opcodeStr = sDataOpNames.Fill;
                        if (dfd.FormatSubType == FormatDescriptor.SubType.Align256 &&
                                GenCommon.CheckJunkAlign(offset, dfd, Project.AddrMap)) {
                            // special syntax for page alignment
                            if (fillVal == 0) {
                                operandStr = "\\";
                            } else {
                                operandStr = "\\," + formatter.FormatHexValue(fillVal, 2);
                            }
                        } else if (length == 1 && fillVal != 0x00) {
                            // Single-byte HEX looks better than "ds 1,$xx", and will match up
                            // with adjacent multi-byte junk/uninit.
                            multiLine = true;
                            opcodeStr = operandStr = null;
                            OutputDenseHex(offset, length, labelStr, commentStr);
                        } else {
                            if (fillVal == 0) {
                                operandStr = length.ToString();
                            } else {
                                operandStr = length + "," + formatter.FormatHexValue(fillVal, 2);
                            }
                        }
                    } else {
                        // treat same as Dense
                        multiLine = true;
                        opcodeStr = operandStr = null;
                        OutputDenseHex(offset, length, labelStr, commentStr);
                    }
                    break;
                case FormatDescriptor.Type.BinaryInclude:
                    opcodeStr = sDataOpNames.BinaryInclude;
                    string biPath = BinaryInclude.ConvertPathNameFromStorage(dfd.Extra);
                    operandStr = biPath;        // no quotes
                    mBinaryIncludes.Add(new BinaryInclude.Excision(offset, length, biPath));
                    break;
                case FormatDescriptor.Type.StringGeneric:
                case FormatDescriptor.Type.StringReverse:
                case FormatDescriptor.Type.StringNullTerm:
                case FormatDescriptor.Type.StringL8:
                case FormatDescriptor.Type.StringL16:
                case FormatDescriptor.Type.StringDci:
                    multiLine = true;
                    opcodeStr = operandStr = null;
                    OutputString(offset, labelStr, commentStr);
                    break;
                default:
                    opcodeStr = "???";
                    operandStr = "***";
                    break;
            }

            if (!multiLine) {
                opcodeStr = formatter.FormatPseudoOp(opcodeStr);
                OutputLine(labelStr, opcodeStr, operandStr, commentStr);
            }
        }

        private void OutputDenseHex(int offset, int length, string labelStr, string commentStr) {
            Formatter formatter = SourceFormatter;
            byte[] data = Project.FileData;
            int maxPerLine = formatter.OperandWrapLen / formatter.CharsPerDenseByte;

            string opcodeStr = formatter.FormatPseudoOp(sDataOpNames.Dense);
            for (int i = 0; i < length; i += maxPerLine) {
                int subLen = length - i;
                if (subLen > maxPerLine) {
                    subLen = maxPerLine;
                }
                string operandStr = formatter.FormatDenseHex(data, offset + i, subLen);

                OutputLine(labelStr, opcodeStr, operandStr, commentStr);
                labelStr = commentStr = string.Empty;
            }
        }

        /// <summary>
        /// Outputs formatted data in an unformatted way, because the code generator couldn't
        /// figure out how to do something better.
        /// </summary>
        private void OutputNoJoy(int offset, int length, string labelStr, string commentStr) {
            byte[] data = Project.FileData;
            Debug.Assert(length > 0);
            Debug.Assert(offset >= 0 && offset < data.Length);

            bool singleValue = true;
            byte val = data[offset];
            for (int i = 1; i < length; i++) {
                if (data[offset + i] != val) {
                    singleValue = false;
                    break;
                }
            }

            if (singleValue && length > 1) {
                string opcodeStr = SourceFormatter.FormatPseudoOp(sDataOpNames.Fill);
                string operandStr = length + "," + SourceFormatter.FormatHexValue(val, 2);
                OutputLine(labelStr, opcodeStr, operandStr, commentStr);
            } else {
                OutputDenseHex(offset, length, labelStr, commentStr);
            }
        }

        // IGenerator
        public string ModifyOpcode(int offset, OpDef op) {
            if (op.IsUndocumented) {
                return null;
            }
            if (Project.CpuDef.Type == CpuDef.CpuType.CpuW65C02) {
                if ((op.Opcode & 0x0f) == 0x07 || (op.Opcode & 0x0f) == 0x0f) {
                    // BBR, BBS, RMB, SMB not supported
                    return null;
                }
            }

            // The assembler works correctly if the symbol is defined as a two-digit hex
            // value (e.g. "foo equ $80") but fails if it's four (e.g. "foo equ $0080").  We
            // output symbols with minimal digits, but this doesn't help if the code itself
            // lives on zero page.  If the operand is a reference to a zero-page user label,
            // we need to output the instruction as hex.
            // More info: https://github.com/apple2accumulator/merlin32/issues/8
            if (op == OpDef.OpPEI_StackDPInd ||
                    op == OpDef.OpSTY_DPIndexX ||
                    op == OpDef.OpSTX_DPIndexY ||
                    op.AddrMode == OpDef.AddressMode.DPIndLong ||
                    op.AddrMode == OpDef.AddressMode.DPInd ||
                    op.AddrMode == OpDef.AddressMode.DPIndexXInd) {
                FormatDescriptor dfd = Project.GetAnattrib(offset).DataDescriptor;
                if (dfd != null && dfd.HasSymbol) {
                    // It has a symbol.  See if the symbol target is a label (auto or user).
                    if (Project.SymbolTable.TryGetValue(dfd.SymbolRef.Label, out Symbol sym)) {
                        if (sym.IsInternalLabel) {
                            return null;
                        }
                    }
                }
            }

            return string.Empty;
        }

        // IGenerator
        public FormatDescriptor ModifyInstructionOperandFormat(int offset, FormatDescriptor dfd,
                int operand) {
            string badChars = ",{}";
            if (dfd.FormatType == FormatDescriptor.Type.NumericLE && dfd.IsStringOrCharacter &&
                    badChars.IndexOf((char)(operand & 0x7f)) >= 0) {
                // Merlin throws an error on certain ASCII operands, e.g. LDA #','
                dfd = FormatDescriptor.Create(dfd.Length,
                    FormatDescriptor.Type.NumericLE, FormatDescriptor.SubType.None);
            }

            return dfd;
        }

        // IGenerator
        public void UpdateCharacterEncoding(FormatDescriptor dfd) { }

        // IGenerator
        public void GenerateShortSequence(int offset, int length, out string opcode,
                out string operand) {
            Debug.Assert(length >= 1 && length <= 4);

            // Use a comma-separated list of individual hex bytes.
            opcode = sDataOpNames.DefineData1;

            StringBuilder sb = new StringBuilder(length * 4);
            for (int i = 0; i < length; i++) {
                if (i != 0) {
                    sb.Append(',');
                }
                sb.Append(SourceFormatter.FormatHexValue(Project.FileData[offset + i], 2));
            }
            operand = sb.ToString();
        }

        // IGenerator
        public void OutputAsmConfig() {
            // nothing to do (though we could emit "xc off" for 6502)
        }

        // IGenerator
        public void OutputEquDirective(string name, string valueStr, string comment) {
            OutputLine(name, SourceFormatter.FormatPseudoOp(sDataOpNames.EquDirective),
                valueStr, SourceFormatter.FormatEolComment(comment));
        }

        // IGenerator
        public void OutputLocalVariableTable(int offset, List<DefSymbol> newDefs,
                LocalVariableTable allDefs) {
            foreach (DefSymbol defSym in newDefs) {
                string valueStr = PseudoOp.FormatNumericOperand(SourceFormatter,
                    Project.SymbolTable, null, defSym.DataDescriptor, defSym.Value, 1,
                    PseudoOp.FormatNumericOpFlags.OmitLabelPrefixSuffix);
                OutputLine(SourceFormatter.FormatVariableLabel(defSym.Label),
                    SourceFormatter.FormatPseudoOp(sDataOpNames.VarDirective),
                    valueStr, SourceFormatter.FormatEolComment(defSym.Comment));
            }
        }

        // IGenerator
        public void OutputArDirective(CommonUtil.AddressMap.AddressChange change) {
            int nextAddress = change.Address;
            if (nextAddress == Address.NON_ADDR) {
                // Start non-addressable regions at zero to ensure they don't overflow bank.
                nextAddress = 0;
            }

            if (change.IsStart) {
                AddressMap.AddressRegion region = change.Region;
                if (region.HasValidPreLabel || region.HasValidIsRelative) {
                    // Need to output the previous ORG, if one is pending.
                    if (mNextAddress >= 0) {
                        OutputLine(string.Empty,
                            SourceFormatter.FormatPseudoOp(sDataOpNames.ArStartDirective),
                            SourceFormatter.FormatHexValue(mNextAddress, 4),
                            string.Empty);
                    }
                }
                if (region.HasValidPreLabel) {
                    string labelStr = mLocalizer.ConvLabel(change.Region.PreLabel);
                    OutputLine(labelStr, string.Empty, string.Empty, string.Empty);
                }
                if (region.HasValidIsRelative) {
                    // Found a valid IsRelative.  Switch to "relative mode" if not there already.
                    mIsInRelative = true;
                }
                if (mIsInRelative) {
                    // Once we see a region with IsRelative set, we output regions as we
                    // find them until the next Flush.
                    string addrStr;
                    if (region.HasValidIsRelative) {
                        Debug.Assert(nextAddress != Address.NON_ADDR &&
                            region.PreLabelAddress != Address.NON_ADDR);
                        int diff = nextAddress - region.PreLabelAddress;
                        string pfxStr;
                        if (diff >= 0) {
                            pfxStr = "*+";
                        } else {
                            pfxStr = "*-";
                            diff = -diff;
                        }
                        addrStr = pfxStr + SourceFormatter.FormatHexValue(diff, 4);
                    } else {
                        addrStr = SourceFormatter.FormatHexValue(nextAddress, 4);
                    }
                    OutputLine(string.Empty,
                        SourceFormatter.FormatPseudoOp(sDataOpNames.ArStartDirective),
                        addrStr, string.Empty);

                    mNextAddress = -1;
                    return;
                }
            }

            mNextAddress = nextAddress;
        }

        // IGenerator
        public void FlushArDirectives() {
            // Output pending directives.  There will always be something to do here unless
            // we were in "relative" mode.
            Debug.Assert(mNextAddress >= 0 || mIsInRelative);
            if (mNextAddress >= 0) {
                OutputLine(string.Empty,
                    SourceFormatter.FormatPseudoOp(sDataOpNames.ArStartDirective),
                    SourceFormatter.FormatHexValue(mNextAddress, 4),
                    string.Empty);
            }
            mNextAddress = -1;
            mIsInRelative = false;
        }

        // IGenerator
        public void OutputRegWidthDirective(int offset, int prevM, int prevX, int newM, int newX) {
            // prevM/prevX may be ambiguous for offset 0, but otherwise everything
            // should be either 0 or 1.
            Debug.Assert(newM == 0 || newM == 1);
            Debug.Assert(newX == 0 || newX == 1);

            if (offset == 0 && newM == 1 && newX == 1) {
                // Assembler defaults to short regs, so we can skip this.
                return;
            }
            OutputLine(string.Empty, SourceFormatter.FormatPseudoOp(REG_WIDTH_DIRECTIVE),
                "%" + newM + newX, string.Empty);
        }

        // IGenerator
        public void OutputLine(string fullLine) {
            mOutStream.WriteLine(fullLine);
        }

        // IGenerator
        public void OutputLine(string label, string opcode, string operand, string comment) {
            // Split long label, but not on EQU directives (confuses the assembler).
            if (!string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(opcode) &&
                    !string.Equals(opcode, sDataOpNames.EquDirective,
                        StringComparison.InvariantCultureIgnoreCase)) {
                if (mLabelNewLine == GenCommon.LabelPlacement.PreferSeparateLine ||
                        (mLabelNewLine == GenCommon.LabelPlacement.SplitIfTooLong &&
                            label.Length >= mColumnWidths[0])) {
                    mOutStream.WriteLine(label);
                    label = string.Empty;
                }
            }

            mLineBuilder.Clear();
            TextUtil.AppendPaddedString(mLineBuilder, label, 0);
            TextUtil.AppendPaddedString(mLineBuilder, opcode, mColumnWidths[0]);
            TextUtil.AppendPaddedString(mLineBuilder, operand,
                mColumnWidths[0] + mColumnWidths[1]);
            TextUtil.AppendPaddedString(mLineBuilder, comment,
                mColumnWidths[0] + mColumnWidths[1] + mColumnWidths[2]);

            mOutStream.WriteLine(mLineBuilder.ToString());
        }


        private void OutputString(int offset, string labelStr, string commentStr) {
            // This gets complicated.
            //
            // For Dci, L8String, and L16String, the entire string needs to fit in the
            // operand of one line.  If it can't, we need to separate the length byte/word
            // or inverted character out, and just dump the rest as ASCII.  Computing the
            // line length requires factoring delimiter character escapes.  (NOTE: contrary
            // to the documentation, STR and STRL do include trailing hex characters in the
            // length calculation, so it's possible to escape delimiters.)
            //
            // For Reverse, we can span lines, but only if we emit the lines in
            // backward order.  Also, Merlin doesn't allow hex to be embedded in a REV
            // operation, so we can't use REV if the string contains a delimiter.
            //
            // For aesthetic purposes, zero-length CString, L8String, and L16String
            // should be output as DFB/DW zeroes rather than an empty string -- makes
            // it easier to read.
            //
            // NOTE: we generally assume that the input is in the correct format, e.g.
            // the length byte in a StringL8 matches dfd.Length, and the high bits in DCI strings
            // have the right pattern.  If not, we will generate bad output.  This would need
            // to be scanned and corrected at a higher level.

            Anattrib attr = Project.GetAnattrib(offset);
            FormatDescriptor dfd = attr.DataDescriptor;
            Debug.Assert(dfd != null);
            Debug.Assert(dfd.IsString);
            Debug.Assert(dfd.Length > 0);

            // We can sort of do parts of C64 stuff, but it's probably more readable to just
            // output a commented blob than something where only the capital letters are readable.
            switch (dfd.FormatSubType) {
                case FormatDescriptor.SubType.Ascii:
                case FormatDescriptor.SubType.HighAscii:
                    break;
                case FormatDescriptor.SubType.C64Petscii:
                case FormatDescriptor.SubType.C64Screen:
                default:
                    OutputNoJoy(offset, dfd.Length, labelStr, commentStr);
                    return;
            }

            Formatter formatter = SourceFormatter;
            byte[] data = Project.FileData;
            StringOpFormatter.ReverseMode revMode = StringOpFormatter.ReverseMode.Forward;
            int leadingBytes = 0;
            string opcodeStr;

            switch (dfd.FormatType) {
                case FormatDescriptor.Type.StringGeneric:
                    opcodeStr = sDataOpNames.StrGeneric;
                    break;
                case FormatDescriptor.Type.StringReverse:
                    opcodeStr = sDataOpNames.StrReverse;
                    revMode = StringOpFormatter.ReverseMode.LineReverse;
                    break;
                case FormatDescriptor.Type.StringNullTerm:
                    opcodeStr = sDataOpNames.StrGeneric;        // no pseudo-op for this
                    if (dfd.Length == 1) {
                        // Empty string.  Just output the length byte(s) or null terminator.
                        GenerateShortSequence(offset, 1, out string opcode, out string operand);
                        OutputLine(labelStr, opcode, operand, commentStr);
                        return;
                    }
                    break;
                case FormatDescriptor.Type.StringL8:
                    opcodeStr = sDataOpNames.StrLen8;
                    leadingBytes = 1;
                    break;
                case FormatDescriptor.Type.StringL16:
                    opcodeStr = sDataOpNames.StrLen16;
                    leadingBytes = 2;
                    break;
                case FormatDescriptor.Type.StringDci:
                    opcodeStr = sDataOpNames.StrDci;
                    break;
                default:
                    Debug.Assert(false);
                    return;
            }

            // Merlin 32 uses single-quote for low ASCII, double-quote for high ASCII.
            CharEncoding.Convert charConv;
            char delim;
            if (dfd.FormatSubType == FormatDescriptor.SubType.HighAscii) {
                charConv = CharEncoding.ConvertHighAscii;
                delim = '"';
            } else {
                charConv = CharEncoding.ConvertAscii;
                delim = '\'';
            }

            StringOpFormatter stropf = new StringOpFormatter(SourceFormatter,
                new Formatter.DelimiterDef(delim),
                StringOpFormatter.RawOutputStyle.DenseHex, charConv, false);
            stropf.IsDciString = (dfd.FormatType == FormatDescriptor.Type.StringDci);

            // Feed bytes in, skipping over the leading length bytes.
            stropf.FeedBytes(data, offset + leadingBytes,
                dfd.Length - leadingBytes, 0, revMode);
            Debug.Assert(stropf.Lines.Count > 0);

            // See if we need to do this over.
            bool redo = false;
            switch (dfd.FormatType) {
                case FormatDescriptor.Type.StringGeneric:
                case FormatDescriptor.Type.StringNullTerm:
                    break;
                case FormatDescriptor.Type.StringReverse:
                    if (stropf.HasEscapedText) {
                        // can't include escaped characters in REV
                        opcodeStr = sDataOpNames.StrGeneric;
                        revMode = StringOpFormatter.ReverseMode.Forward;
                        redo = true;
                    }
                    break;
                case FormatDescriptor.Type.StringL8:
                    if (stropf.Lines.Count != 1) {
                        // single-line only
                        opcodeStr = sDataOpNames.StrGeneric;
                        leadingBytes = 1;
                        redo = true;
                    }
                    break;
                case FormatDescriptor.Type.StringL16:
                    if (stropf.Lines.Count != 1) {
                        // single-line only
                        opcodeStr = sDataOpNames.StrGeneric;
                        leadingBytes = 2;
                        redo = true;
                    }
                    break;
                case FormatDescriptor.Type.StringDci:
                    if (stropf.Lines.Count != 1) {
                        // single-line only
                        opcodeStr = sDataOpNames.StrGeneric;
                        stropf.IsDciString = false;
                        redo = true;
                    }
                    break;
                default:
                    Debug.Assert(false);
                    return;
            }

            if (redo) {
                //Debug.WriteLine("REDO off=+" + offset.ToString("x6") + ": " + dfd.FormatType);

                // This time, instead of skipping over leading length bytes, we include them
                // explicitly.
                stropf.Reset();
                stropf.FeedBytes(data, offset, dfd.Length, leadingBytes, revMode);
            }

            opcodeStr = formatter.FormatPseudoOp(opcodeStr);

            foreach (string str in stropf.Lines) {
                OutputLine(labelStr, opcodeStr, str, commentStr);
                labelStr = commentStr = string.Empty;       // only show on first
            }
        }
    }

    #endregion IGenerator


    #region IAssembler

    /// <summary>
    /// Cross-assembler execution interface.
    /// </summary>
    public class AsmMerlin32 : IAssembler {
        // Paths from generator.
        private List<string> mPathNames;

        // Directory to make current before executing assembler.
        private string mWorkDirectory;


        // IAssembler
        public void GetExeIdentifiers(out string humanName, out string exeName) {
            humanName = "Merlin Assembler";
            exeName = "Merlin32";
        }

        // IAssembler
        public AssemblerConfig GetDefaultConfig() {
            return new AssemblerConfig(string.Empty, new int[] { 9, 6, 11, 74 });
        }

        // IAssembler
        public AssemblerVersion QueryVersion() {
            AssemblerConfig config =
                AssemblerConfig.GetConfig(AppSettings.Global, AssemblerInfo.Id.Merlin32);
            if (config == null || string.IsNullOrEmpty(config.ExecutablePath)) {
                return null;
            }

            ShellCommand cmd = new ShellCommand(config.ExecutablePath, string.Empty,
                Directory.GetCurrentDirectory(), null);
            cmd.Execute();
            if (string.IsNullOrEmpty(cmd.Stdout)) {
                return null;
            }

            // Stdout: "C:\Src\WorkBench\Merlin32.exe v 1.0, (c) Brutal Deluxe ..."
            // Other platforms may not have the ".exe".  Find first occurrence of " v ".

            const string PREFIX = " v ";    // not expecting this to appear in the path
            string str = cmd.Stdout;
            int start = str.IndexOf(PREFIX);
            int end = (start < 0) ? -1 : str.IndexOf(',', start);

            if (start < 0 || end < 0 || start + PREFIX.Length >= end) {
                Debug.WriteLine("Couldn't find version in " + str);
                return null;
            }
            start += PREFIX.Length;
            string versionStr = str.Substring(start, end - start);
            CommonUtil.Version version = CommonUtil.Version.Parse(versionStr);
            if (!version.IsValid) {
                return null;
            }
            return new AssemblerVersion(versionStr, version);
        }

        // IAssembler
        public void Configure(GenerationResults results, string workDirectory) {
            // Clone pathNames, in case the caller decides to modify the original.
            mPathNames = CommonUtil.Container.CopyStringList(results.PathNames);
            mWorkDirectory = workDirectory;
        }

        // IAssembler
        public AssemblerResults RunAssembler(BackgroundWorker worker) {
            // Reduce input file to a partial path if possible.  This is really just to make
            // what we display to the user a little easier to read.
            string pathName = mPathNames[0];
            if (pathName.StartsWith(mWorkDirectory)) {
                pathName = pathName.Remove(0, mWorkDirectory.Length + 1);
            } else {
                // Unexpected, but shouldn't be a problem.
                Debug.WriteLine("NOTE: source file is not in work directory");
            }

            AssemblerConfig config =
                AssemblerConfig.GetConfig(AppSettings.Global, AssemblerInfo.Id.Merlin32);
            if (string.IsNullOrEmpty(config.ExecutablePath)) {
                Debug.WriteLine("Assembler not configured");
                return null;
            }

            worker.ReportProgress(0, Res.Strings.PROGRESS_ASSEMBLING);

            // Wrap pathname in quotes in case it has spaces.
            // (Do we need to shell-escape quotes in the pathName?)
            //
            // Merlin 32 has no options.  The second argument is the macro include file path.
            ShellCommand cmd = new ShellCommand(config.ExecutablePath, ". \"" + pathName + "\"",
                mWorkDirectory, null);
            cmd.Execute();

            // Can't really do anything with a "cancel" request.

            // Output filename is the input filename without the ".S".  Since the filename
            // was generated by us we can be confident in the format.
            string outputFile = mPathNames[0].Substring(0, mPathNames[0].Length - 2);

            return new AssemblerResults(cmd.FullCommandLine, cmd.ExitCode, cmd.Stdout,
                cmd.Stderr, outputFile);
        }
    }

    #endregion IAssembler
}
