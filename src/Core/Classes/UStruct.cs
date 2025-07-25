﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using UELib.Branch;
using UELib.Branch.UE2.Eon;
using UELib.Core.Tokens;
using UELib.Flags;

namespace UELib.Core
{
    /// <summary>
    /// Represents a unreal struct with the functionality to contain Constants, Enums, Structs and Properties.
    /// </summary>
    [UnrealRegisterClass]
    public partial class UStruct : UField
    {
        [Obsolete] public const int VInterfaceClass = 222;

        #region Serialized Members

        public UTextBuffer? ScriptText { get; private set; }
        public UTextBuffer? ProcessedText { get; private set; }
        public UTextBuffer? CppText { get; private set; }
        public UName FriendlyName { get; protected set; }

        public int Line;
        public int TextPos;

        public UnrealFlags<StructFlag> StructFlags;

        protected UField? Children { get; private set; }
        protected int DataScriptSize { get; private set; }
        private int ByteScriptSize { get; set; }

        #endregion

        #region Script Members

        [Obsolete]
        public IEnumerable<UConst> Constants => EnumerateFields<UConst>();

        [Obsolete]
        public IEnumerable<UEnum> Enums => EnumerateFields<UEnum>();

        [Obsolete]
        public IEnumerable<UStruct> Structs => EnumerateFields<UStruct>().Where(obj => obj.IsPureStruct());

        [Obsolete]
        public IEnumerable<UProperty> Variables => EnumerateFields<UProperty>();

        [Obsolete]
        public IEnumerable<UProperty> Locals => EnumerateFields<UProperty>().Where(prop => !prop.IsParm());

        #endregion

        #region General Members

        /// <summary>
        /// Default Properties buffer offset
        /// </summary>
        protected long _DefaultPropertiesOffset;

        //protected uint _CodePosition;

        public long ScriptOffset { get; private set; }
        public int ScriptSize { get; private set; }

        public UByteCodeDecompiler? ByteCodeManager;

        #endregion

        #region Constructors

        protected override void Deserialize()
        {
            base.Deserialize();

            if (_Buffer.Version >= (uint)PackageObjectLegacyVersion.SuperReferenceMovedToUStruct)
            {
                Super = _Buffer.ReadObject<UStruct>();
                Record(nameof(Super), Super);

                // Weird bug in UDK (805 and 810), where the Super is set to Commandlet
                // Set to null to prevent an infinite loop in EnumerateSuper
                if (Super != null && Name == "Object" && Super.Name == "Commandlet")
                {
                    Super = null;
                }
            }
#if BATMAN
            if (Package.Build == UnrealPackage.GameBuild.BuildName.Batman4)
            {
                goto skipScriptText;
            }
#endif
#if BORDERLANDS
            // Swapped order...
            if (Package.Build == UnrealPackage.GameBuild.BuildName.Borderlands)
            {
                Children = _Buffer.ReadObject<UField>();
                Record(nameof(Children), Children);

                ScriptText = _Buffer.ReadObject<UTextBuffer>();
                Record(nameof(ScriptText), ScriptText);

                // FIXME: another 2x32 uints here (IsConsoleCooked)

                goto skipChildren;
            }
#endif
#if SWRepublicCommando
            if (_Buffer.Package.Build == UnrealPackage.GameBuild.BuildName.SWRepublicCommando)
            {
                Super = _Buffer.ReadObject<UStruct>();
                Record(nameof(Super), Super);
            }
#endif
            if (!Package.IsConsoleCooked() && _Buffer.UE4Version < 117)
            {
                ScriptText = _Buffer.ReadObject<UTextBuffer>();
                Record(nameof(ScriptText), ScriptText);
            }

        skipScriptText:
            Children = _Buffer.ReadObject<UField>();
            Record(nameof(Children), Children);
        skipChildren:
#if BATMAN
            if (Package.Build == UnrealPackage.GameBuild.BuildName.Batman4)
            {
                goto serializeByteCode;
            }
#endif
#if ADVENT
            if (Package.Build == UnrealPackage.GameBuild.BuildName.Advent)
            {
                // Preload fields so we can tinker with NextField.
                for (var field = Children; field != null; field = field.NextField)
                {
                    if (field is { DeserializationState: 0 })
                    {
                        field.Load();
                    }
                }

                var tail = EnumerateFields().LastOrDefault();
                UProperty? property;

                do
                {
                    property = EonEngineBranch.SerializeFProperty<UProperty>(_Buffer);
                    if (tail == null)
                    {
                        Children = property;
                    }
                    else
                    {
                        tail.NextField = property;
                    }

                    tail = property;
                } while (property != null);

                if (_Buffer.Version >= 133)
                {
                    goto skipFriendlyName;
                }
            }
#endif
#if SWRepublicCommando
            if (Package.Build == UnrealPackage.GameBuild.BuildName.SWRepublicCommando && Children != null)
            {
                var tail = Children;
                while (true)
                {
                    var nextField = _Buffer.ReadObject<UField?>();
                    Record(nameof(nextField), nextField);

                    if (nextField == null)
                    {
                        break;
                    }

                    tail.NextField = nextField;
                    tail = nextField;
                }
            }
#endif
            // Moved to UFunction in UE3
            if (_Buffer.Version < (uint)PackageObjectLegacyVersion.MovedFriendlyNameToUFunction)
            {
                FriendlyName = _Buffer.ReadNameReference();
                Record(nameof(FriendlyName), FriendlyName);
            }
        skipFriendlyName:
#if DNF
            if (Package.Build == UnrealPackage.GameBuild.BuildName.DNF)
            {
                if (_Buffer.LicenseeVersion >= 17)
                {
                    // Back-ported CppText
                    CppText = _Buffer.ReadObject<UTextBuffer>();
                    Record(nameof(CppText), CppText);

                    var dnfTextObj2 = _Buffer.ReadObject();
                    Record(nameof(dnfTextObj2), dnfTextObj2);

                    _Buffer.ReadArray(out UArray<UObject> dnfIncludeTexts);
                    Record(nameof(dnfIncludeTexts), dnfIncludeTexts);
                }

                if (_Buffer.LicenseeVersion >= 2)
                {
                    // Bool?
                    byte dnfByte = _Buffer.ReadByte();
                    Record(nameof(dnfByte), dnfByte);

                    var dnfName = _Buffer.ReadNameReference();
                    Record(nameof(dnfName), dnfName);
                }

                goto lineData;
            }
#endif
            // Standard, but UT2004' derived games do not include this despite reporting version 128+
            if (_Buffer.Version >= (uint)PackageObjectLegacyVersion.AddedCppTextToUStruct &&
                _Buffer.UE4Version < 117 &&
                !Package.IsConsoleCooked() &&
                (Package.Build != BuildGeneration.UE2_5 &&
                 Package.Build != BuildGeneration.AGP))
            {
                CppText = _Buffer.ReadObject<UTextBuffer>();
                Record(nameof(CppText), CppText);
            }
#if VENGEANCE
            // Introduced with BioShock
            if (Package.Build == BuildGeneration.Vengeance &&
                _Buffer.LicenseeVersion >= 29)
            {
                int vengeanceUnknownObject = _Buffer.ReadObjectIndex();
                Record(nameof(vengeanceUnknownObject), vengeanceUnknownObject);
            }
#endif
            // UE3 or UE2.5 build, it appears that StructFlags may have been merged from an early UE3 build.
            // UT2004 reports version 26, and BioShock version 2
            if ((Package.Build == BuildGeneration.UE2_5 && _Buffer.LicenseeVersion >= 26) ||
                (Package.Build == BuildGeneration.AGP && _Buffer.LicenseeVersion >= 17) ||
                (Package.Build == BuildGeneration.Vengeance && _Buffer.LicenseeVersion >= 2)
#if SG1
                // Same offset and version check as CppText (120) probably an incorrectly back-ported feature.
                || (Package.Build == UnrealPackage.GameBuild.BuildName.SG1_TA && _Buffer.Version >= 120)
#endif
               )
            {
                _Buffer.Read(out StructFlags);
                Record(nameof(StructFlags), StructFlags);
            }
#if VENGEANCE
            if (Package.Build == BuildGeneration.Vengeance &&
                _Buffer.LicenseeVersion >= 14)
            {
                ProcessedText = _Buffer.ReadObject<UTextBuffer>();
                Record(nameof(ProcessedText), ProcessedText);
            }
#endif
        lineData:
            if (!Package.IsConsoleCooked() &&
                _Buffer.UE4Version < 117)
            {
                Line = _Buffer.ReadInt32();
                Record(nameof(Line), Line);
                TextPos = _Buffer.ReadInt32();
                Record(nameof(TextPos), TextPos);
            }

            // FIXME: Version >= 130 (According to SWRepublic && Version < 200 (RoboHordes, EndWar, R6Vegas)
            // Guarded with SWRepublicCommando, because it is the only supported game that has this particular change.
            if (Package.Build == UnrealPackage.GameBuild.BuildName.SWRepublicCommando &&
                _Buffer.Version >= 130 &&
                _Buffer.Version < 200)
            {
                uint minAlignment = _Buffer.ReadUInt32(); // v60
                Record(nameof(minAlignment), minAlignment);
            }
#if UNREAL2
            if (Package.Build == UnrealPackage.GameBuild.BuildName.Unreal2)
            {
                // Always zero in all of the Core.u structs
                int unknownInt32 = _Buffer.ReadInt32();
                Record("Unknown:Unreal2", unknownInt32);
            }
#endif
#if TRANSFORMERS
            if (Package.Build == BuildGeneration.HMS)
            {
                int transformersEndLine = _Buffer.ReadInt32();
                // The line where the struct's code body ends.
                Record(nameof(transformersEndLine), transformersEndLine);
            }
#endif
#if SPLINTERCELLX
            // Probably a backport mistake, this should appear before Line and TextPos
            if (Package.Build == BuildGeneration.SCX &&
                _Buffer.LicenseeVersion >= 39)
            {
                CppText = _Buffer.ReadObject<UTextBuffer>();
                Record(nameof(CppText), CppText);
            }
#endif
#if LEAD
            // Same as SCX
            if (Package.Build == BuildGeneration.Lead)
            {
                CppText = _Buffer.ReadObject<UTextBuffer>(); // v34
                Record(nameof(CppText), CppText);

                string v64 = _Buffer.ReadString();
                Record(nameof(v64), v64);
            }
#endif
        serializeByteCode:
            ByteScriptSize = _Buffer.ReadInt32();
            Record(nameof(ByteScriptSize), ByteScriptSize);

            if (_Buffer.Version >= (uint)PackageObjectLegacyVersion.AddedDataScriptSizeToUStruct)
            {
                DataScriptSize = _Buffer.ReadInt32();
                Record(nameof(DataScriptSize), DataScriptSize);
            }
            else
            {
                DataScriptSize = ByteScriptSize;
            }

            ScriptOffset = _Buffer.Position;

            // Code Statements
            if (DataScriptSize > 0)
            {
                ByteCodeManager = new UByteCodeDecompiler(this);
                if (_Buffer.Version >= (uint)PackageObjectLegacyVersion.AddedDataScriptSizeToUStruct)
                {
                    _Buffer.Skip(DataScriptSize);
                }
                else
                {
                    ByteCodeManager.Deserialize();
                }

                _Buffer.ConformRecordPosition();
                ScriptSize = (int)(_Buffer.Position - ScriptOffset);
            }
#if DNF
            if (Package.Build == UnrealPackage.GameBuild.BuildName.DNF)
            {
                //_Buffer.ReadByte();
            }
#endif
            // StructDefaults in RoboHordes (200)
            if (_Buffer.Version >= (uint)PackageObjectLegacyVersion.UE3
                && GetType() == typeof(UStruct))
            {
                DeserializeProperties(_Buffer);
            }
        }

        protected override bool CanDisposeBuffer()
        {
            return base.CanDisposeBuffer() && ByteCodeManager == null;
        }

        [Obsolete("Deprecated", true)]
        protected void FindChildren()
        {
            throw new NotImplementedException("Use EnumerateFields");
        }

        #endregion

        public IEnumerable<UField> EnumerateFields()
        {
            for (var field = Children; field != null; field = field.NextField)
            {
                yield return field;
            }
        }

        public IEnumerable<T> EnumerateFields<T>()
            where T : UField
        {
            for (var field = Children; field != null; field = field.NextField)
            {
                if (field is T tField)
                {
                    yield return tField;
                }
            }
        }

        public T? FindProperty<T>(UName name)
            where T : UProperty
        {
            UProperty property = null;

            foreach (var super in EnumerateSuper(this))
            {
                foreach (var field in super.EnumerateFields<T>())
                {
                    if (field.Name == name)
                    {
                        return field;
                    }
                }
            }

            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TokenFactory GetTokenFactory()
        {
            return Package.Branch.GetTokenFactory(Package);
        }

        [Obsolete("Use StructFlags directly")]
        public bool HasStructFlag(StructFlags flag)
        {
            return (StructFlags & (uint)flag) != 0;
        }

        internal bool HasStructFlag(StructFlag flagIndex)
        {
            return StructFlags.HasFlag(Package.Branch.EnumFlagsMap[typeof(StructFlag)], flagIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsPureStruct()
        {
            return IsClassType("Struct") || IsClassType("ScriptStruct");
        }
    }
}
