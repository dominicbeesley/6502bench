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
    /// Generate source code compatible with the ACME assembler
    /// (https://sourceforge.net/projects/acme-crossass/).
    /// </summary>
    public class GenAcme : IGenerator {
        // The ACME docs say that ACME sources should use the ".a" extension.  However, this
        // is already used for static libraries on UNIX systems, which means filename
        // completion in shells tends to ignore them, and it can cause confusion in
        // makefile rules.  Since ".S" is pretty universal for assembly language sources,
        // I'm sticking with that.
        private const string ASM_FILE_SUFFIX = "_acme.S"; // must start with underscore

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
        /// Output mode; determines how ORG is handled.
        /// </summary>
        private enum OutputMode {
            Unknown = 0, Loadable = 1, Streamable = 2
        }
        private OutputMode mOutputMode;

        /// <summary>
        /// Current pseudo-PC depth.  0 is the "real" PC.
        /// </summary>
        private int mPcDepth;
        private bool mFirstIsOpen;

        /// <summary>
        /// Holds detected version of configured assembler.
        /// </summary>
        private CommonUtil.Version mAsmVersion = CommonUtil.Version.NO_VERSION;

        // Version we're coded against.
        private static CommonUtil.Version V0_96_4 = new CommonUtil.Version(0, 96, 4);
        private static CommonUtil.Version V0_97 = new CommonUtil.Version(0, 97);

        // v0.97 started treating '\' in constants as an escape character.
        private bool mBackslashEscapes = true;


        // Pseudo-op string constants.
        private static PseudoOp.PseudoOpNames sDataOpNames =
            new PseudoOp.PseudoOpNames(new Dictionary<string, string> {
                { "EquDirective", "=" },
                //VarDirective
                { "ArStartDirective", "!pseudopc" },
                { "ArEndDirective", "}" },
                //RegWidthDirective         // !al, !as, !rl, !rs
                //DataBankDirective
                { "DefineData1", "!byte" },
                { "DefineData2", "!word" },
                { "DefineData3", "!24" },
                { "DefineData4", "!32" },
                //DefineBigData2
                //DefineBigData3
                //DefineBigData4
                { "Fill", "!fill" },
                { "Dense", "!hex" },
                { "Uninit", "!skip" },
                //Junk
                { "Align", "!align" },
                { "BinaryInclude", "!binary" },
                { "StrGeneric", "!text" },       // can use !xor for high ASCII
                //StrReverse
                //StrNullTerm
                //StrLen8
                //StrLen16
                //StrDci
        });


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
                mAsmVersion = V0_97;                    // No assembler installed, use default.
            }

            // ACME isn't a single-pass assembler, but the code that determines label widths
            // only runs in the first pass and doesn't get corrected.  So unlike cc65, which
            // generates correct zero-page acceses once the label's value is known, ACME
            // uses 16-bit addressing to zero-page labels for backward references if there
            // are any forward references at all.  The easy way to deal with this is to make
            // all zero-page label references have explicit widths.
            //
            // Example:
            // *       =       $1000
            //         jmp     zero
            //         !pseudopc $0000 {
            // zero    nop
            //         lda     zero
            //         rts
            //         }
            Quirks.SinglePassAssembler = true;
            Quirks.SinglePassNoLabelCorrection = true;
            if (mAsmVersion < V0_97) {
                Quirks.BlockMoveArgsNoHash = true;
                mBackslashEscapes = false;
            }

            mWorkDirectory = workDirectory;
            mFileNameBase = fileNameBase;
            Settings = settings;

            mLabelNewLine = Settings.GetEnum(AppSettings.SRCGEN_LABEL_NEW_LINE,
                    GenCommon.LabelPlacement.SplitIfTooLong);

            AssemblerConfig config = AssemblerConfig.GetConfig(settings,
                AssemblerInfo.Id.Acme);
            mColumnWidths = (int[])config.ColumnWidths.Clone();

            // ACME wants the entire file to be loadable into a 64KB memory area.  If the
            // initial address is too large, a file smaller than 64KB might overrun the bank
            // boundary and cause a failure.  In that case we want to set the initial address
            // to zero and "stream" the rest.
            int firstAddr = project.AddrMap.OffsetToAddress(0);
            if (firstAddr == Address.NON_ADDR) {
                firstAddr = 0;
            }
            if (firstAddr + project.FileDataLength > 65536) {
                mOutputMode = OutputMode.Streamable;
            } else {
                mOutputMode = OutputMode.Loadable;
            }
        }

        /// <summary>
        /// Configures the assembler-specific format items.
        /// </summary>
        private void SetFormatConfigValues(ref Formatter.FormatConfig config) {
            config.SuppressImpliedAcc = true;       // implied acc not allowed

            config.OperandWrapLen = 64;
            config.ForceDirectOpcodeSuffix = "+1";
            config.ForceAbsOpcodeSuffix = "+2";
            config.ForceLongOpcodeSuffix = "+3";
            config.ForceDirectOperandPrefix = string.Empty;
            config.ForceAbsOperandPrefix = string.Empty;
            config.ForceLongOperandPrefix = string.Empty;
            config.LocalVariableLabelPrefix = ".";
            config.EndOfLineCommentDelimiter = ";";
            config.FullLineCommentDelimiterBase = ";";
            config.NonUniqueLabelPrefix = "@";
            config.CommaSeparatedDense = false;
            config.ExprMode = Formatter.FormatConfig.ExpressionMode.Common;

            Formatter.DelimiterSet charSet = new Formatter.DelimiterSet();
            charSet.Set(CharEncoding.Encoding.Ascii, Formatter.SINGLE_QUOTE_DELIM);
            charSet.Set(CharEncoding.Encoding.HighAscii,
                new Formatter.DelimiterDef(string.Empty, '\'', '\'', " | $80"));
            config.CharDelimiters = charSet;
        }

        // IGenerator
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
            // While '.' labels are limited to the current zone, '@' labels are visible
            // between global labels.  (This is poorly documented.)
            mLocalizer.LocalPrefix = "@";
            mLocalizer.QuirkNoOpcodeMnemonics = true;
            mLocalizer.ReservedWords = new List<string>() { "NOT" };
            mLocalizer.Analyze();

            mPcDepth = 0;
            mFirstIsOpen = true;

            // Use UTF-8 encoding, without a byte-order mark.
            using (StreamWriter sw = new StreamWriter(pathName, false, new UTF8Encoding(false))) {
                mOutStream = sw;

                if (Settings.GetBool(AppSettings.SRCGEN_ADD_IDENT_COMMENT, false)) {
                    OutputLine(SourceFormatter.FullLineCommentDelimiterPlus +
                        string.Format(Res.Strings.GENERATED_FOR_VERSION_FMT,
                        "acme", mAsmVersion, AsmAcme.OPTIONS));
                }

                if (HasNonZeroBankCode()) {
                    // don't try
                    OutputLine(SourceFormatter.FullLineCommentDelimiterPlus +
                        "ACME can't handle 65816 code that lives outside bank zero");
                    int firstAddr = Project.AddrMap.OffsetToAddress(0);
                    AddressMap.AddressRegion fakeRegion = new AddressMap.AddressRegion(0,
                        Project.FileData.Length, firstAddr);
                    OutputArDirective(new AddressMap.AddressChange(true,
                        0, firstAddr, fakeRegion, true));
                    OutputDenseHex(0, Project.FileData.Length, string.Empty, string.Empty);
                    OutputArDirective(new AddressMap.AddressChange(false,
                        0, firstAddr, fakeRegion, true));
                } else {
                    GenCommon.Generate(this, sw, worker);
                }
            }
            mOutStream = null;

            return new GenerationResults(pathNames, string.Empty, mBinaryIncludes);
        }

        /// <summary>
        /// Determines whether the project has any code assembled outside bank zero.
        /// </summary>
        private bool HasNonZeroBankCode() {
            if (Project.CpuDef.HasAddr16) {
                // Not possible on this CPU.
                return false;
            }
            foreach (AddressMap.AddressMapEntry ent in Project.AddrMap) {
                if (ent.Address > 0xffff) {
                    return true;
                }
            }
            return false;
        }

        // IGenerator
        public void OutputAsmConfig() {
            CpuDef cpuDef = Project.CpuDef;
            string cpuStr;
            if (cpuDef.Type == CpuDef.CpuType.Cpu65816) {
                cpuStr = "65816";
            } else if (cpuDef.Type == CpuDef.CpuType.Cpu65C02) {
                cpuStr = "65c02";
            } else if (cpuDef.Type == CpuDef.CpuType.CpuW65C02) {
                cpuStr = "w65c02";
            } else if (cpuDef.Type == CpuDef.CpuType.Cpu6502 && cpuDef.HasUndocumented) {
                cpuStr = "6510";
            } else {
                cpuStr = "6502";
            }

            OutputLine(string.Empty, SourceFormatter.FormatPseudoOp("!cpu"), cpuStr, string.Empty);
        }

        // IGenerator
        public string ModifyOpcode(int offset, OpDef op) {
            if (op.IsUndocumented) {
                if (Project.CpuDef.Type == CpuDef.CpuType.Cpu65C02 ||
                        Project.CpuDef.Type == CpuDef.CpuType.CpuW65C02) {
                    // none of the "LDD" stuff is handled
                    return null;
                }
                if ((op.Mnemonic == OpName.ANC && op.Opcode != 0x0b) ||
                        (op.Mnemonic == OpName.JAM && op.Opcode != 0x02)) {
                    // There are multiple opcodes that match the mnemonic.  Output the
                    // mnemonic for the first one and hex for the rest.
                    return null;
                } else if (op.Mnemonic == OpName.NOP || op.Mnemonic == OpName.DOP ||
                        op.Mnemonic == OpName.TOP) {
                    // the various undocumented no-ops aren't handled
                    return null;
                } else if (op.Mnemonic == OpName.SBC) {
                    // this is the alternate reference to SBC
                    return null;
                } else if (op == OpDef.OpALR_Imm) {
                    // ACME wants "ASR" instead for $4b
                    return "asr";
                } else if (op == OpDef.OpLAX_Imm) {
                    // ACME spits out an error on $ab
                    return null;
                }
            }
            if (op == OpDef.OpWDM_WDM || op == OpDef.OpBRK_StackInt) {
                // ACME doesn't like these to have an operand.  Output as hex.
                return null;
            }
            return string.Empty;        // indicate original is fine
        }

        // IGenerator
        public FormatDescriptor ModifyInstructionOperandFormat(int offset, FormatDescriptor dfd,
                int operand) {
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
                    operandStr = PseudoOp.FormatNumericOperand(formatter, Project.SymbolTable,
                        mLocalizer.LabelMap, dfd, operand, length,
                        PseudoOp.FormatNumericOpFlags.OmitLabelPrefixSuffix);
                    break;
                case FormatDescriptor.Type.NumericBE:
                    opcodeStr = sDataOpNames.GetDefineBigData(length);
                    if (string.IsNullOrEmpty(opcodeStr)) {
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
                    operandStr = length + "," + formatter.FormatHexValue(data[offset], 2);
                    break;
                case FormatDescriptor.Type.Dense:
                    multiLine = true;
                    opcodeStr = operandStr = null;
                    OutputDenseHex(offset, length, labelStr, commentStr);
                    break;
                case FormatDescriptor.Type.Uninit:
                case FormatDescriptor.Type.Junk:
                    bool canAlign = (dfd.FormatType == FormatDescriptor.Type.Junk);
                    int fillVal = Helper.CheckRangeHoldsSingleValue(data, offset, length);
                    if (canAlign && fillVal >= 0 &&
                            GenCommon.CheckJunkAlign(offset, dfd, Project.AddrMap)) {
                        // !align ANDVALUE, EQUALVALUE [, FILLVALUE]
                        opcodeStr = sDataOpNames.Align;
                        int alignVal = 1 << FormatDescriptor.AlignmentToPower(dfd.FormatSubType);
                        operandStr = (alignVal - 1).ToString() +
                            ",0," + formatter.FormatHexValue(fillVal, 2);
                    } else if (fillVal >= 0 && (length > 1 || fillVal == 0x00)) {
                        // If multi-byte, or single byte and zero, treat same as Fill.
                        opcodeStr = sDataOpNames.Fill;
                        operandStr = length + "," + formatter.FormatHexValue(fillVal, 2);
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
                    operandStr = '"' + biPath + '"';
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
        public void OutputEquDirective(string name, string valueStr, string comment) {
            OutputLine(name, SourceFormatter.FormatPseudoOp(sDataOpNames.EquDirective),
                valueStr, SourceFormatter.FormatEolComment(comment));
        }

        // IGenerator
        public void OutputLocalVariableTable(int offset, List<DefSymbol> newDefs,
                LocalVariableTable allDefs) {
            // We can do better here, but it requires knowing whether anything in "newDefs"
            // overwrote a previous entry.  If everything is new, we don't need to start
            // a new zone, and can just output newDefs.  (We don't need to start a new zone
            // on a "clear previous".)
            OutputLine(string.Empty, "!zone", "Z" + offset.ToString("x6"), string.Empty);
            for (int i = 0; i < allDefs.Count; i++) {
                DefSymbol defSym = allDefs[i];

                string valueStr = PseudoOp.FormatNumericOperand(SourceFormatter,
                    Project.SymbolTable, null, defSym.DataDescriptor, defSym.Value, 1,
                    PseudoOp.FormatNumericOpFlags.OmitLabelPrefixSuffix);
                OutputEquDirective(SourceFormatter.FormatVariableLabel(defSym.Label),
                    valueStr, defSym.Comment);
            }
        }

        // IGenerator
        public void OutputArDirective(CommonUtil.AddressMap.AddressChange change) {
            // This is similar in operation to the AsmTass64 implementation.  See comments there.
            Debug.Assert(mPcDepth >= 0);
            int nextAddress = change.Address;
            if (nextAddress == Address.NON_ADDR) {
                // Start non-addressable regions at zero to ensure they don't overflow bank.
                nextAddress = 0;
            }
            if (change.IsStart) {
                if (change.Region.HasValidPreLabel) {
                    string labelStr = mLocalizer.ConvLabel(change.Region.PreLabel);
                    OutputLine(labelStr, string.Empty, string.Empty, string.Empty);
                }
                if (mPcDepth == 0  && mFirstIsOpen) {
                    mPcDepth++;

                    // Set the "real" PC for the first address change.  If we're in "loadable"
                    // mode, just set "*=".  If we're in "streaming" mode, we set "*=" to zero
                    // and then use a pseudo-PC.
                    if (mOutputMode == OutputMode.Loadable) {
                        OutputLine("*", "=", SourceFormatter.FormatHexValue(nextAddress, 4),
                            string.Empty);
                        return;
                    } else {
                        // set the real PC to address zero to ensure we get a full 64KB
                        OutputLine("*", "=", SourceFormatter.FormatHexValue(0, 4), string.Empty);
                    }
                }
                AddressMap.AddressRegion region = change.Region;
                string addrStr;
                if (region.HasValidIsRelative) {
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
                    addrStr + " {",
                    string.Empty);
                mPcDepth++;
            } else {
                mPcDepth--;
                if (mPcDepth > 0 || !mFirstIsOpen) {
                    // close previous block
                    OutputLine(string.Empty,
                        SourceFormatter.FormatPseudoOp(sDataOpNames.ArEndDirective),
                        string.Empty, string.Empty);
                        //";" + SourceFormatter.FormatPseudoOp(sDataOpNames.ArStartDirective));
                } else {
                    // mark initial "*=" region as closed, but don't output anything
                    mFirstIsOpen = false;
                }
            }
        }

        // IGenerator
        public void FlushArDirectives() { }

        // IGenerator
        public void OutputRegWidthDirective(int offset, int prevM, int prevX, int newM, int newX) {
            if (prevM != newM) {
                string mop = (newM == 0) ? "!al" : "!as";
                OutputLine(string.Empty, SourceFormatter.FormatPseudoOp(mop),
                    string.Empty, string.Empty);
            }
            if (prevX != newX) {
                string xop = (newX == 0) ? "!rl" : "!rs";
                OutputLine(string.Empty, SourceFormatter.FormatPseudoOp(xop),
                    string.Empty, string.Empty);
            }
        }

        // IGenerator
        public void OutputLine(string fullLine) {
            mOutStream.WriteLine(fullLine);
        }

        // IGenerator
        public void OutputLine(string label, string opcode, string operand, string comment) {
            // Break the line if the label is long and it's not a .EQ directive.
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
            Formatter formatter = SourceFormatter;
            byte[] data = Project.FileData;
            Anattrib attr = Project.GetAnattrib(offset);
            FormatDescriptor dfd = attr.DataDescriptor;
            Debug.Assert(dfd != null);
            Debug.Assert(dfd.IsString);
            Debug.Assert(dfd.Length > 0);

            string opcodeStr;
            CharEncoding.Convert charConv;
            switch (dfd.FormatSubType) {
                case FormatDescriptor.SubType.Ascii:
                    opcodeStr = sDataOpNames.StrGeneric;
                    charConv = CharEncoding.ConvertAscii;
                    break;
                case FormatDescriptor.SubType.HighAscii:
                    opcodeStr = sDataOpNames.StrGeneric;
                    charConv = CharEncoding.ConvertHighAscii;
                    break;
                case FormatDescriptor.SubType.C64Petscii:
                    opcodeStr = "!pet";
                    charConv = CharEncoding.ConvertC64Petscii;
                    break;
                case FormatDescriptor.SubType.C64Screen:
                    opcodeStr = "!scr";
                    charConv = CharEncoding.ConvertC64ScreenCode;
                    break;
                default:
                    Debug.Assert(false);
                    OutputNoJoy(offset, dfd.Length, labelStr, commentStr);
                    return;
            }

            int leadingBytes = 0;

            switch (dfd.FormatType) {
                case FormatDescriptor.Type.StringGeneric:
                case FormatDescriptor.Type.StringReverse:
                case FormatDescriptor.Type.StringNullTerm:
                case FormatDescriptor.Type.StringDci:
                    // Last byte may be output as hex.
                    break;
                case FormatDescriptor.Type.StringL8:
                    // Length byte will be output as hex.
                    leadingBytes = 1;
                    break;
                case FormatDescriptor.Type.StringL16:
                    // Length byte will be output as hex.
                    leadingBytes = 2;
                    break;
                default:
                    Debug.Assert(false);
                    return;
            }

            StringOpFormatter stropf = new StringOpFormatter(SourceFormatter,
                Formatter.DOUBLE_QUOTE_DELIM, StringOpFormatter.RawOutputStyle.CommaSep, charConv,
                mBackslashEscapes);
            stropf.FeedBytes(data, offset, dfd.Length, leadingBytes,
                StringOpFormatter.ReverseMode.Forward);

            if (dfd.FormatSubType == FormatDescriptor.SubType.HighAscii && stropf.HasEscapedText) {
                // Can't !xor the output, because while it works for string data it
                // also flips the high bits on the unprintable bytes we output as raw hex.
                // We'd need to tell the string formatter to flip the high bit on the byte.
                OutputNoJoy(offset, dfd.Length, labelStr, commentStr);
                return;
            }

            if (dfd.FormatSubType == FormatDescriptor.SubType.HighAscii) {
                OutputLine(string.Empty, "!xor", "$80 {", string.Empty);
            }
            foreach (string str in stropf.Lines) {
                OutputLine(labelStr, opcodeStr, str, commentStr);
                labelStr = commentStr = string.Empty;       // only show on first
            }
            if (dfd.FormatSubType == FormatDescriptor.SubType.HighAscii) {
                OutputLine(string.Empty, "}", string.Empty, string.Empty);
            }
        }
    }

    #endregion IGenerator


    #region IAssembler

    /// <summary>
    /// Cross-assembler execution interface.
    /// </summary>
    public class AsmAcme : IAssembler {
        public const string OPTIONS = "";

        // Paths from generator.
        private List<string> mPathNames;

        // Directory to make current before executing assembler.
        private string mWorkDirectory;


        // IAssembler
        public void GetExeIdentifiers(out string humanName, out string exeName) {
            humanName = "ACME Assembler";
            exeName = "acme";
        }

        // IAssembler
        public AssemblerConfig GetDefaultConfig() {
            return new AssemblerConfig(string.Empty, new int[] { 8, 8, 11, 73 });
        }

        // IAssembler
        public AssemblerVersion QueryVersion() {
            AssemblerConfig config =
                AssemblerConfig.GetConfig(AppSettings.Global, AssemblerInfo.Id.Acme);
            if (config == null || string.IsNullOrEmpty(config.ExecutablePath)) {
                return null;
            }

            ShellCommand cmd = new ShellCommand(config.ExecutablePath, "--version",
                Directory.GetCurrentDirectory(), null);
            cmd.Execute();
            if (string.IsNullOrEmpty(cmd.Stdout)) {
                return null;
            }

            // Windows - Stdout: "This is ACME, release 0.96.4 ("Fenchurch"), 22 Dec 2017 ..."
            // Linux - Stderr:   "This is ACME, release 0.96.4 ("Fenchurch"), 20 Apr 2019 ..."

            const string PREFIX = "release ";
            string str = cmd.Stdout;
            int start = str.IndexOf(PREFIX);
            int end = (start < 0) ? -1 : str.IndexOf(' ', start + PREFIX.Length + 1);

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
                AssemblerConfig.GetConfig(AppSettings.Global, AssemblerInfo.Id.Acme);
            if (string.IsNullOrEmpty(config.ExecutablePath)) {
                Debug.WriteLine("Assembler not configured");
                return null;
            }

            worker.ReportProgress(0, Res.Strings.PROGRESS_ASSEMBLING);

            // Output file name is source file name with the ".a".
            string outFileName = pathName.Substring(0, pathName.Length - 2);

            // Wrap pathname in quotes in case it has spaces.
            // (Do we need to shell-escape quotes in the pathName?)
            ShellCommand cmd = new ShellCommand(config.ExecutablePath,
                OPTIONS + " -o \"" + outFileName + "\"" + " \"" + pathName + "\"" ,
                mWorkDirectory, null);
            cmd.Execute();

            // Can't really do anything with a "cancel" request.

            // Output filename is the input filename without the ".a".  Since the filename
            // was generated by us we can be confident in the format.
            string outputFile = mPathNames[0].Substring(0, mPathNames[0].Length - 2);

            return new AssemblerResults(cmd.FullCommandLine, cmd.ExitCode, cmd.Stdout,
                cmd.Stderr, outputFile);
        }
    }

    #endregion IAssembler
}
