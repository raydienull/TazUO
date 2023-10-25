﻿#region license

// Copyright (c) 2021, andreakarasho
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
// 1. Redistributions of source code must retain the above copyright
//    notice, this list of conditions and the following disclaimer.
// 2. Redistributions in binary form must reproduce the above copyright
//    notice, this list of conditions and the following disclaimer in the
//    documentation and/or other materials provided with the distribution.
// 3. All advertising materials mentioning features or use of this software
//    must display the following acknowledgement:
//    This product includes software developed by andreakarasho - https://github.com/andreakarasho
// 4. Neither the name of the copyright holder nor the
//    names of its contributors may be used to endorse or promote products
//    derived from this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS ''AS IS'' AND ANY
// EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE FOR ANY
// DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
// (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
// ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
// SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#endregion

using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ClassicUO.Assets
{
    public unsafe class AnimationsLoader : UOFileLoader
    {
        public const int MAX_ACTIONS = 80; // gargoyle is like 78
        public const int MAX_DIRECTIONS = 5;

        private static AnimationsLoader _instance;
        private static BodyConvFlags _lastFlags = (BodyConvFlags)(-1);

        [ThreadStatic]
        private static FrameInfo[] _frames;

        [ThreadStatic]
        private static byte[] _decompressedData;

        private readonly Dictionary<ushort, Dictionary<ushort, EquipConvData>> _equipConv =
            new Dictionary<ushort, Dictionary<ushort, EquipConvData>>();
        private readonly UOFileMul[] _files = new UOFileMul[5];
        private readonly UOFileUop[] _filesUop = new UOFileUop[4];

        private readonly Dictionary<int, MobTypeInfo> _mobTypes =
            new Dictionary<int, MobTypeInfo>();
        private readonly Dictionary<int, BodyInfo> _bodyInfos = new Dictionary<int, BodyInfo>();
        private readonly Dictionary<int, BodyInfo> _corpseInfos = new Dictionary<int, BodyInfo>();
        private readonly Dictionary<int, BodyConvInfo> _bodyConvInfos =
            new Dictionary<int, BodyConvInfo>();
        private readonly Dictionary<int, UopInfo> _uopInfos = new Dictionary<int, UopInfo>();

        private AnimationsLoader() { }

        public static AnimationsLoader Instance =>
            _instance ?? (_instance = new AnimationsLoader());

        public IReadOnlyDictionary<ushort, Dictionary<ushort, EquipConvData>> EquipConversions =>
            _equipConv;

        struct MobTypeInfo
        {
            public ANIMATION_GROUPS_TYPE Type;
            public ANIMATION_FLAGS Flags;
        }

        public List<Tuple<ushort, byte>>[] GroupReplaces { get; } =
            new List<Tuple<ushort, byte>>[2]
            {
                new List<Tuple<ushort, byte>>(),
                new List<Tuple<ushort, byte>>()
            };

        private unsafe void LoadInternal()
        {
            bool loaduop = false;
            int[] un = { 0x40000, 0x10000, 0x20000, 0x20000, 0x20000 };

            for (int i = 0; i < 5; i++)
            {
                string pathmul = UOFileManager.GetUOFilePath(
                    "anim" + (i == 0 ? string.Empty : (i + 1).ToString()) + ".mul"
                );

                string pathidx = UOFileManager.GetUOFilePath(
                    "anim" + (i == 0 ? string.Empty : (i + 1).ToString()) + ".idx"
                );

                if (File.Exists(pathmul) && File.Exists(pathidx))
                {
                    _files[i] = new UOFileMul(pathmul, pathidx, un[i], i == 0 ? 6 : -1);
                }

                if (i > 0 && UOFileManager.IsUOPInstallation)
                {
                    string pathuop = UOFileManager.GetUOFilePath($"AnimationFrame{i}.uop");

                    if (File.Exists(pathuop))
                    {
                        _filesUop[i - 1] = new UOFileUop(
                            pathuop,
                            "build/animationlegacyframe/{0:D6}/{0:D2}.bin"
                        );

                        if (!loaduop)
                        {
                            loaduop = true;
                        }
                    }
                }
            }

            if (loaduop)
            {
                LoadUop();
            }

            if (UOFileManager.Version >= ClientVersion.CV_500A)
            {
                string path = UOFileManager.GetUOFilePath("mobtypes.txt");

                if (File.Exists(path))
                {
                    var typeNames = new string[5]
                    {
                        "monster",
                        "sea_monster",
                        "animal",
                        "human",
                        "equipment"
                    };

                    using (var reader = new StreamReader(File.OpenRead(path)))
                    {
                        string line;

                        while ((line = reader.ReadLine()) != null)
                        {
                            line = line.Trim();

                            if (line.Length == 0 || line[0] == '#' || !char.IsNumber(line[0]))
                            {
                                continue;
                            }

                            string[] parts = line.Split(
                                new[] { '\t', ' ' },
                                StringSplitOptions.RemoveEmptyEntries
                            );

                            if (parts.Length < 3)
                            {
                                continue;
                            }

                            int id = int.Parse(parts[0]);
                            string testType = parts[1].ToLower();
                            int commentIdx = parts[2].IndexOf('#');

                            if (commentIdx > 0)
                            {
                                parts[2] = parts[2].Substring(0, commentIdx - 1);
                            }
                            else if (commentIdx == 0)
                            {
                                continue;
                            }

                            uint number = uint.Parse(parts[2], NumberStyles.HexNumber);

                            for (int i = 0; i < 5; i++)
                            {
                                if (
                                    testType.Equals(
                                        typeNames[i],
                                        StringComparison.InvariantCultureIgnoreCase
                                    )
                                )
                                {
                                    _mobTypes[id] = new MobTypeInfo()
                                    {
                                        Type = (ANIMATION_GROUPS_TYPE)i,
                                        Flags = (ANIMATION_FLAGS)(0x80000000 | number)
                                    };

                                    break;
                                }
                            }
                        }
                    }
                }
            }

            string file = UOFileManager.GetUOFilePath("Anim1.def");

            if (File.Exists(file))
            {
                using (DefReader defReader = new DefReader(file))
                {
                    while (defReader.Next())
                    {
                        ushort group = (ushort)defReader.ReadInt();

                        if (group == 0xFFFF)
                        {
                            continue;
                        }

                        int replace = defReader.ReadGroupInt();

                        GroupReplaces[0].Add(new Tuple<ushort, byte>(group, (byte)replace));
                    }
                }
            }

            file = UOFileManager.GetUOFilePath("Anim2.def");

            if (File.Exists(file))
            {
                using (DefReader defReader = new DefReader(file))
                {
                    while (defReader.Next())
                    {
                        ushort group = (ushort)defReader.ReadInt();

                        if (group == 0xFFFF)
                        {
                            continue;
                        }

                        int replace = defReader.ReadGroupInt();

                        GroupReplaces[1].Add(new Tuple<ushort, byte>(group, (byte)replace));
                    }
                }
            }

            ProcessEquipConvDef();
            ProcessBodyDef();
            ProcessCorpseDef();
        }

        public bool ReplaceBody(ref ushort body, ref ushort hue)
        {
            if (_bodyInfos.TryGetValue(body, out var bodyInfo))
            {
                body = bodyInfo.Graphic;
                hue = bodyInfo.Hue;

                return true;
            }

            return false;
        }

        public ReadOnlySpan<AnimIdxBlock> GetIndices(ref ushort body, ref ushort hue, ref ANIMATION_FLAGS flags, out int fileIndex, out ANIMATION_GROUPS_TYPE animType)
        {
            fileIndex = 0;
            animType = ANIMATION_GROUPS_TYPE.UNKNOWN;

            if (!_mobTypes.TryGetValue(body, out var mobInfo))
            {
                return ReadOnlySpan<AnimIdxBlock>.Empty;
            }

            flags = mobInfo.Flags;

            if (mobInfo.Flags.HasFlag(ANIMATION_FLAGS.AF_USE_UOP_ANIMATION))
            {
                // TODO: calculate UOP stuff
                return ReadOnlySpan<AnimIdxBlock>.Empty;
            }

            if (_bodyConvInfos.TryGetValue(body, out var bodyConvInfo))
            {
                hue = bodyConvInfo.Hue;
                body = bodyConvInfo.Graphic;
                fileIndex = bodyConvInfo.FileIndex;
                animType = bodyConvInfo.AnimType;
            }

            if (animType == ANIMATION_GROUPS_TYPE.UNKNOWN)
                animType = mobInfo.Type != ANIMATION_GROUPS_TYPE.UNKNOWN ? mobInfo.Type : CalculateTypeByGraphic(body);

            var fileIdx = _files[fileIndex].IdxFile;
            var offsetAddress = CalculateOffset(body, animType, flags, out var actionCount);

            var animIdxSpan = new ReadOnlySpan<AnimIdxBlock>(
                (byte*)(fileIdx.StartAddress.ToInt64() + offsetAddress),
                actionCount * MAX_DIRECTIONS
            );

            return animIdxSpan;
        }

        public ReadOnlySpan<AnimIdxBlock> LoadAnimIndex(
            ref int fileIndex,
            ref ushort graphic
        )
        {
            if (fileIndex < 0 || fileIndex >= _files.Length)
                return ReadOnlySpan<AnimIdxBlock>.Empty;

            _mobTypes.TryGetValue(graphic, out var mobInfo);
            var animType = mobInfo.Type != ANIMATION_GROUPS_TYPE.UNKNOWN ? mobInfo.Type : CalculateTypeByGraphic(graphic);

            if (_bodyInfos.TryGetValue(graphic, out var bodyInfo))
            {
                if (fileIndex <= 0)
                    graphic = bodyInfo.Graphic;
            }

            if (_bodyConvInfos.TryGetValue(graphic, out var bodyConvInfo))
            {
                if (fileIndex <= 0)
                {
                    fileIndex = (byte)bodyConvInfo.FileIndex;
                    animType = bodyConvInfo.AnimType;
                    graphic = bodyConvInfo.Graphic;
                }
            }

            var fileIdx = _files[fileIndex].IdxFile;
            var offsetAddress = CalculateOffset(graphic, animType, mobInfo.Flags, out var actionCount);

            var animIdxSpan = new ReadOnlySpan<AnimIdxBlock>(
                (byte*)(fileIdx.StartAddress.ToInt64() + offsetAddress),
                actionCount * MAX_DIRECTIONS
            );

            return animIdxSpan;

            //foreach (ref readonly var aidx in animIdxSpan)
            //{
            //    if (aidx.Size != 0 && aidx.Position != 0xFFFFFFFF && aidx.Size != 0xFFFFFFFF)
            //    {
            //        // TODO: set Position and Size
            //    }
            //}
        }

        public long CalculateOffset(
            ushort graphic,
            ANIMATION_GROUPS_TYPE type,
            ANIMATION_FLAGS flags,
            out int groupCount
        )
        {
            long result = 0;
            groupCount = 0;

            var group = ANIMATION_GROUPS.AG_NONE;

            switch (type)
            {
                case ANIMATION_GROUPS_TYPE.MONSTER:

                    if ((flags & ANIMATION_FLAGS.AF_CALCULATE_OFFSET_BY_PEOPLE_GROUP) != 0)
                    {
                        group = ANIMATION_GROUPS.AG_PEOPLE;
                    }
                    else if ((flags & ANIMATION_FLAGS.AF_CALCULATE_OFFSET_BY_LOW_GROUP) != 0)
                    {
                        group = ANIMATION_GROUPS.AG_LOW;
                    }
                    else
                    {
                        group = ANIMATION_GROUPS.AG_HIGHT;
                    }

                    break;

                case ANIMATION_GROUPS_TYPE.SEA_MONSTER:
                    result = CalculateHighGroupOffset(graphic);
                    groupCount = (int)LOW_ANIMATION_GROUP.LAG_ANIMATION_COUNT;

                    break;

                case ANIMATION_GROUPS_TYPE.ANIMAL:

                    if ((flags & ANIMATION_FLAGS.AF_CALCULATE_OFFSET_LOW_GROUP_EXTENDED) != 0)
                    {
                        if ((flags & ANIMATION_FLAGS.AF_CALCULATE_OFFSET_BY_PEOPLE_GROUP) != 0)
                        {
                            group = ANIMATION_GROUPS.AG_PEOPLE;
                        }
                        else if ((flags & ANIMATION_FLAGS.AF_CALCULATE_OFFSET_BY_LOW_GROUP) != 0)
                        {
                            group = ANIMATION_GROUPS.AG_LOW;
                        }
                        else
                        {
                            group = ANIMATION_GROUPS.AG_HIGHT;
                        }
                    }
                    else
                    {
                        group = ANIMATION_GROUPS.AG_LOW;
                    }

                    break;

                default:
                    group = ANIMATION_GROUPS.AG_PEOPLE;

                    break;
            }

            switch (group)
            {
                case ANIMATION_GROUPS.AG_LOW:
                    result = CalculateLowGroupOffset(graphic);
                    groupCount = (int)LOW_ANIMATION_GROUP.LAG_ANIMATION_COUNT;

                    break;

                case ANIMATION_GROUPS.AG_HIGHT:
                    result = CalculateHighGroupOffset(graphic);
                    groupCount = (int)HIGHT_ANIMATION_GROUP.HAG_ANIMATION_COUNT;

                    break;

                case ANIMATION_GROUPS.AG_PEOPLE:
                    result = CalculatePeopleGroupOffset(graphic);
                    groupCount = (int)PEOPLE_ANIMATION_GROUP.PAG_ANIMATION_COUNT;

                    break;
            }

            return result;
        }

        public override unsafe Task Load()
        {
            return Task.Run(LoadInternal);
        }

        private void ProcessEquipConvDef()
        {
            if (UOFileManager.Version < ClientVersion.CV_300)
            {
                return;
            }

            var file = UOFileManager.GetUOFilePath("Equipconv.def");

            if (File.Exists(file))
            {
                using (DefReader defReader = new DefReader(file, 5))
                {
                    while (defReader.Next())
                    {
                        ushort body = (ushort)defReader.ReadInt();
                        ushort graphic = (ushort)defReader.ReadInt();
                        ushort newGraphic = (ushort)defReader.ReadInt();
                        int gump = defReader.ReadInt();

                        if (gump > ushort.MaxValue)
                        {
                            continue;
                        }

                        if (gump == 0)
                        {
                            gump = graphic;
                        }
                        else if (gump == 0xFFFF || gump == -1)
                        {
                            gump = newGraphic;
                        }

                        ushort color = (ushort)defReader.ReadInt();

                        if (!_equipConv.TryGetValue(body, out var dict))
                        {
                            _equipConv[body] = (dict = new Dictionary<ushort, EquipConvData>());
                        }

                        dict[graphic] = new EquipConvData(newGraphic, (ushort)gump, color);
                    }
                }
            }
        }

        public void ProcessBodyConvDef(BodyConvFlags flags)
        {
            if (UOFileManager.Version < ClientVersion.CV_300)
            {
                return;
            }

            var file = UOFileManager.GetUOFilePath("Bodyconv.def");

            if (!File.Exists(file))
                return;

            using (var defReader = new DefReader(file))
            {
                while (defReader.Next())
                {
                    ushort index = (ushort)defReader.ReadInt();

                    for (int i = 1; i < defReader.PartsCount; i++)
                    {
                        int body = defReader.ReadInt();
                        if (body < 0)
                        {
                            continue;
                        }

                        // Ensure the client is allowed to use these new graphics
                        if (i == 1)
                        {
                            if (!flags.HasFlag(BodyConvFlags.Anim1))
                            {
                                continue;
                            }
                        }
                        else if (i == 2)
                        {
                            if (!flags.HasFlag(BodyConvFlags.Anim2))
                            {
                                continue;
                            }
                        }

                        // NOTE: for fileindex >= 3 the client automatically accepts body conversion.
                        //       Probably it ignores the flags
                        /*else if (i == 3)
                        {
                            if (flags.HasFlag(BodyConvFlags.Anim3))
                            {
                                continue;
                            }
                        }
                        else if (i == 4)
                        {
                            if (flags.HasFlag(BodyConvFlags.Anim4))
                            {
                                continue;
                            }
                        }
                        */

                        sbyte mountedHeightOffset = 0;
                        if (i == 1)
                        {
                            if (index == 0x00C0 || index == 793)
                            {
                                mountedHeightOffset = -9;
                            }
                        }
                        else if (i == 2)
                        {
                            if (index == 0x0579)
                            {
                                mountedHeightOffset = 9;
                            }
                        }
                        else if (i == 4)
                        {
                            mountedHeightOffset = -9;

                            if (index == 0x0115 || index == 0x00C0)
                            {
                                mountedHeightOffset = 0;
                            }
                            else if (index == 0x042D)
                            {
                                mountedHeightOffset = 3;
                            }
                        }

                        if (i >= _files.Length || _files[i] == null)
                        {
                            continue;
                        }

                        UOFile currentIdxFile = _files[i].IdxFile;

                        // ANIMATION_GROUPS_TYPE realType =
                        //     UOFileManager.Version < ClientVersion.CV_500A
                        //         ? CalculateTypeByGraphic((ushort)body, i)
                        //         : _dataIndex[index].Type;

                        _bodyConvInfos[index] = new BodyConvInfo()
                        {
                            FileIndex = i,
                            Graphic = (ushort)body,
                            // TODO: fix for UOFileManager.Version < ClientVersion.CV_500A
                            AnimType = CalculateTypeByGraphic((ushort)body, i)
                        };

                        // long addressOffset = _dataIndex[index].CalculateOffset(
                        //     (ushort)body,
                        //     realType,
                        //     out int count
                        // );

                        // count = Math.Min(count, MAX_ACTIONS);

                        // if (addressOffset < currentIdxFile.Length)
                        // {
                        //     _dataIndex[index].Graphic = (ushort)body;
                        //     _dataIndex[index].Type = realType;

                        //     if (_dataIndex[index].MountedHeightOffset == 0)
                        //     {
                        //         _dataIndex[index].MountedHeightOffset = mountedHeightOffset;
                        //     }

                        //     _dataIndex[index].FileIndex = (byte)i;

                        //     bool isValid = false;
                        //     addressOffset += currentIdxFile.StartAddress.ToInt64();
                        //     long maxaddress =
                        //         currentIdxFile.StartAddress.ToInt64() + currentIdxFile.Length;

                        //     int offset = 0;

                        //     if (_dataIndex[index].Groups == null)
                        //     {
                        //         _dataIndex[index].Groups = new AnimationGroup[MAX_ACTIONS];
                        //     }

                        //     for (int j = 0; j < count; j++)
                        //     {
                        //         if (_dataIndex[index].Groups[j] == null)
                        //         {
                        //             _dataIndex[index].Groups[j] = new AnimationGroup();
                        //         }

                        //         for (byte d = 0; d < MAX_DIRECTIONS; d++)
                        //         {
                        //             AnimIdxBlock* aidx = (AnimIdxBlock*)(
                        //                 addressOffset + offset * sizeof(AnimIdxBlock)
                        //             );

                        //             ++offset;

                        //             if (
                        //                 (long)aidx < maxaddress
                        //                 && aidx->Position != 0xFFFFFFFF
                        //                 && aidx->Size != 0xFFFFFFFF
                        //             )
                        //             {
                        //                 ref var direction = ref _dataIndex[index].Groups[
                        //                     j
                        //                 ].Direction[d];
                        //                 direction.Address = aidx->Position;
                        //                 direction.Size = Math.Max(1, aidx->Size);

                        //                 isValid = true;
                        //             }
                        //             else
                        //             {
                        //                 // we need to nullify the bad address or we will read invalid data.
                        //                 // we dont touch the isValid parameter because sometime the mul exists but some frames don't
                        //                 // see: https://github.com/ClassicUO/ClassicUO/issues/1532
                        //                 ref var direction = ref _dataIndex[index].Groups[
                        //                     j
                        //                 ].Direction[d];
                        //                 direction.Address = 0;
                        //                 direction.Size = 0;
                        //             }
                        //         }
                        //     }

                        //     _dataIndex[index].IsValidMUL = isValid;

                        //     break;
                        // }
                    }
                }
            }
        }

        private void ProcessBodyDef()
        {
            if (UOFileManager.Version < ClientVersion.CV_300)
            {
                return;
            }

            var file = UOFileManager.GetUOFilePath("Body.def");

            if (!File.Exists(file))
                return;

            using (var defReader = new DefReader(file, 1))
            {
                while (defReader.Next())
                {
                    int index = defReader.ReadInt();

                    if (_bodyInfos.TryGetValue(index, out var info) && info.Graphic != 0)
                    {
                        continue;
                    }

                    int[] group = defReader.ReadGroup();

                    if (group == null)
                    {
                        continue;
                    }

                    int color = defReader.ReadInt();

                    //Yes, this is actually how this is supposed to work.
                    var checkIndex = group.Length >= 3 ? group[2] : group[0];

                    _bodyInfos[index] = new BodyInfo()
                    {
                        Graphic = (ushort)checkIndex,
                        Hue = (ushort)color
                    };
                }
            }
        }

        private void ProcessCorpseDef()
        {
            if (UOFileManager.Version < ClientVersion.CV_300)
            {
                return;
            }

            var file = UOFileManager.GetUOFilePath("Corpse.def");

            if (!File.Exists(file))
                return;

            using (var defReader = new DefReader(file, 1))
            {
                while (defReader.Next())
                {
                    int index = defReader.ReadInt();

                    if (_corpseInfos.TryGetValue(index, out var b) && b.Graphic != 0)
                    {
                        continue;
                    }

                    int[] group = defReader.ReadGroup();

                    if (group == null)
                    {
                        continue;
                    }

                    int color = defReader.ReadInt();
                    int checkIndex = group.Length >= 3 ? group[2] : group[0];

                    _corpseInfos[index] = new BodyInfo()
                    {
                        Graphic = (ushort)checkIndex,
                        Hue = (ushort)color
                    };
                }
            }
        }

        private void LoadUop()
        {
            if (UOFileManager.Version <= ClientVersion.CV_60144)
            {
                return;
            }

            // for (ushort animID = 0; animID < _dataIndex.Length; animID++)
            // {
            //     for (byte grpID = 0; grpID < MAX_ACTIONS; grpID++)
            //     {
            //         string hashstring = $"build/animationlegacyframe/{animID:D6}/{grpID:D2}.bin";
            //         ulong hash = UOFileUop.CreateHash(hashstring);

            //         for (int i = 0; i < _filesUop.Length; i++)
            //         {
            //             UOFileUop uopFile = _filesUop[i];

            //             if (uopFile != null && uopFile.TryGetUOPData(hash, out UOFileIndex data))
            //             {
            //                 if (_dataIndex[animID] == null)
            //                 {
            //                     _dataIndex[animID] = new IndexAnimation
            //                     {
            //                         UopGroups = new AnimationGroupUop[MAX_ACTIONS]
            //                     };
            //                 }

            //                 _dataIndex[animID].InitializeUOP();

            //                 ref AnimationGroupUop g = ref _dataIndex[animID].UopGroups[grpID];

            //                 g = new AnimationGroupUop
            //                 {
            //                     Offset = (uint)data.Offset,
            //                     CompressedLength = (uint)data.Length,
            //                     DecompressedLength = (uint)data.DecompressedLength,
            //                     FileIndex = i,
            //                 };
            //             }
            //         }
            //     }
            // }

            for (int i = 0; i < _filesUop.Length; i++)
            {
                _filesUop[i]?.ClearHashes();
            }

            string animationSequencePath = UOFileManager.GetUOFilePath("AnimationSequence.uop");

            if (!File.Exists(animationSequencePath))
            {
                Log.Warn("AnimationSequence.uop not found");

                return;
            }

            // credit: @tristran
            // u32 animid
            // 12 times: [
            //   u32 unk0 //often zero
            // ]
            // //--------------
            // u32 replace
            // replace times: [
            //   u32 oldgroup
            //   u32 framecount
            //   u32 newgroup
            //   //if newgroup not is -1 then this animation group is replaced by that group
            //   u32 flags1 //unsure what these mean often 0x41100000
            //   16 times: [ //maybe something to do with mounts but...
            //     u8 unk1 //if newgroup ==-1 usually -128 else usually 0
            //   ]
            //   8 times: [
            //     u32 unk2 //often 0 animation 826 has something different...
            //   ]
            //   u32 num1     //rarely present but human (400) has them for oldgroup 0,1,2,3,23,24,35 (stand/walk/run)
            //   num1 times: [
            //     u32 w0
            //     u32 w1
            //     u32 w2
            //     u32 w3
            //     u32 w4
            //     u32 w5
            //     u16 s6
            //     u32 w7
            //     u16 s8
            //   ]
            //   u32 num2
            //   num2 times: [
            //     u32 unk3
            //   ]
            // ]
            // //-----------
            // u32 xtra
            // xtra times: [
            //   u8 mob_mode //identifies the "mode" this defintion belongs to combat(0)/id(1)/ride (2) /fly(3)/fly combat?(4)/fly idle(5) /sit(6)
            //   s8 b2       //fallback mode?
            //   u32 def_action // default action (fallback if action not in following structure)
            //   u32 num1
            //   num1 times: [ //transition to other mode? (see gargoyle (666))
            //     u8 b6     //mode
            //     u32 n5    //anim/group
            //   ]
            //   u32 num2    //usually 3 (stand, walk, run)
            //   num2 times: [
            //     u8 action
            //     u32 anim1h //one handed?
            //     u32 anim2h //two handed?
            //   ]
            //   u32 num3    // NewCharacterAnimation
            //   num3 times: [
            //     s8 type      //actual action fight etc
            //     s8 action    //sub action
            //     u32 num4     //random select one of the list
            //     num4 times: [
            //       u32 anim   //group
            //     ]
            //   ]
            // ]

            /*
            based on the current "mode" the mobile is in (e.g. IsFlying check) select the right set of definitions from the xtra array
            then consult the num2 based list for stand/walk/run
            and the num3 based list for NewCharacterAnimation packets
            /
            / flags
            41100000
            41400000 usually group 22,24 (walk run?)
            40C00000 often group 31
            42860000 anim 692   Animated weapon
            41F80000 anim 692
            41300000 anim 1246,1247 , group 0  (jack o lantern)
            */

            var animSeq = new UOFileUop(
                animationSequencePath,
                "build/animationsequence/{0:D8}.bin"
            );
            var animseqEntries = new UOFileIndex[animSeq.TotalEntriesCount];
            animSeq.FillEntries(ref animseqEntries);

            Span<byte> spanAlloc = stackalloc byte[1024];

            for (int i = 0; i < animseqEntries.Length; i++)
            {
                ref var entry = ref animseqEntries[i];

                if (entry.Offset == 0)
                {
                    continue;
                }

                animSeq.Seek(entry.Offset);

                byte[] buffer = null;

                Span<byte> span =
                    entry.DecompressedLength <= 1024
                        ? spanAlloc
                        : (
                            buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(
                                entry.DecompressedLength
                            )
                        );

                try
                {
                    fixed (byte* destPtr = span)
                    {
                        ZLib.Decompress(
                            animSeq.PositionAddress,
                            entry.Length,
                            0,
                            (IntPtr)destPtr,
                            entry.DecompressedLength
                        );
                    }

                    var reader = new StackDataReader(span.Slice(0, entry.DecompressedLength));

                    uint animID = reader.ReadUInt32LE();
                    reader.Skip(48);
                    int replaces = reader.ReadInt32LE();

                    var uopInfo = new UopInfo();
                    var replacedAnimSpan = uopInfo.ReplacedAnimations;
                    for (var j = 0; j < replacedAnimSpan.Length; ++j)
                        replacedAnimSpan[j] = j;

                    if (replaces != 48 && replaces != 68)
                    {
                        for (int k = 0; k < replaces; k++)
                        {
                            int oldGroup = reader.ReadInt32LE();
                            uint frameCount = reader.ReadUInt32LE();
                            int newGroup = reader.ReadInt32LE();

                            if (frameCount == 0)
                            {
                                replacedAnimSpan[oldGroup] = newGroup;
                            }

                            reader.Skip(60);
                        }

                        if (
                            animID == 0x04E7
                            || animID == 0x042D
                            || animID == 0x04E6
                            || animID == 0x05F7
                            || animID == 0x05A1
                        )
                        {
                            uopInfo.HeightOffset = 18;
                        }
                        else if (
                            animID == 0x01B0
                            || animID == 0x0579
                            || animID == 0x05F6
                            || animID == 0x05A0
                        )
                        {
                            uopInfo.HeightOffset = 9;
                        }
                    }

                    _uopInfos.Add((int)animID, uopInfo);

                    reader.Release();
                }
                finally
                {
                    if (buffer != null)
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }

            animSeq.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe uint CalculatePeopleGroupOffset(ushort graphic)
        {
            return (uint)(((graphic - 400) * 175 + 35000) * sizeof(AnimIdxBlock));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe uint CalculateHighGroupOffset(ushort graphic)
        {
            return (uint)(graphic * 110 * sizeof(AnimIdxBlock));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe uint CalculateLowGroupOffset(ushort graphic)
        {
            return (uint)(((graphic - 200) * 65 + 22000) * sizeof(AnimIdxBlock));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ANIMATION_GROUPS_TYPE CalculateTypeByGraphic(ushort graphic, int fileIndex = 0)
        {
            if (fileIndex == 1) // anim2
            {
                return graphic < 200 ? ANIMATION_GROUPS_TYPE.MONSTER : ANIMATION_GROUPS_TYPE.ANIMAL;
            }

            if (fileIndex == 2) // anim3
            {
                return graphic < 300
                    ? ANIMATION_GROUPS_TYPE.ANIMAL
                    : graphic < 400
                        ? ANIMATION_GROUPS_TYPE.MONSTER
                        : ANIMATION_GROUPS_TYPE.HUMAN;
            }

            return graphic < 200
                ? ANIMATION_GROUPS_TYPE.MONSTER
                : graphic < 400
                    ? ANIMATION_GROUPS_TYPE.ANIMAL
                    : ANIMATION_GROUPS_TYPE.HUMAN;
        }

        public override void ClearResources() { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetAnimDirection(ref byte dir, ref bool mirror)
        {
            switch (dir)
            {
                case 2:
                case 4:
                    mirror = dir == 2;
                    dir = 1;

                    break;

                case 1:
                case 5:
                    mirror = dir == 1;
                    dir = 2;

                    break;

                case 0:
                case 6:
                    mirror = dir == 0;
                    dir = 3;

                    break;

                case 3:
                    dir = 0;

                    break;

                case 7:
                    dir = 4;

                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GetSittingAnimDirection(ref byte dir, ref bool mirror, ref int x, ref int y)
        {
            switch (dir)
            {
                case 0:
                    mirror = true;
                    dir = 3;

                    break;

                case 2:
                    mirror = true;
                    dir = 1;

                    break;

                case 4:
                    mirror = false;
                    dir = 1;

                    break;

                case 6:
                    mirror = false;
                    dir = 3;

                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FixSittingDirection(
            ref byte direction,
            ref bool mirror,
            ref int x,
            ref int y,
            ref SittingInfoData data
        )
        {
            switch (direction)
            {
                case 7:
                case 0:
                {
                    if (data.Direction1 == -1)
                    {
                        if (direction == 7)
                        {
                            direction = (byte)data.Direction4;
                        }
                        else
                        {
                            direction = (byte)data.Direction2;
                        }
                    }
                    else
                    {
                        direction = (byte)data.Direction1;
                    }

                    break;
                }

                case 1:
                case 2:
                {
                    if (data.Direction2 == -1)
                    {
                        if (direction == 1)
                        {
                            direction = (byte)data.Direction1;
                        }
                        else
                        {
                            direction = (byte)data.Direction3;
                        }
                    }
                    else
                    {
                        direction = (byte)data.Direction2;
                    }

                    break;
                }

                case 3:
                case 4:
                {
                    if (data.Direction3 == -1)
                    {
                        if (direction == 3)
                        {
                            direction = (byte)data.Direction2;
                        }
                        else
                        {
                            direction = (byte)data.Direction4;
                        }
                    }
                    else
                    {
                        direction = (byte)data.Direction3;
                    }

                    break;
                }

                case 5:
                case 6:
                {
                    if (data.Direction4 == -1)
                    {
                        if (direction == 5)
                        {
                            direction = (byte)data.Direction3;
                        }
                        else
                        {
                            direction = (byte)data.Direction1;
                        }
                    }
                    else
                    {
                        direction = (byte)data.Direction4;
                    }

                    break;
                }
            }

            GetSittingAnimDirection(ref direction, ref mirror, ref x, ref y);

            const int SITTING_OFFSET_X = 8;

            int offsX = SITTING_OFFSET_X;

            if (mirror)
            {
                if (direction == 3)
                {
                    y += 25 + data.MirrorOffsetY;
                    x += offsX - 4;
                }
                else
                {
                    y += data.OffsetY + 9;
                }
            }
            else
            {
                if (direction == 3)
                {
                    y += 23 + data.MirrorOffsetY;
                    x -= 3;
                }
                else
                {
                    y += 10 + data.OffsetY;
                    x -= offsX + 1;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ANIMATION_GROUPS GetGroupIndex(ushort graphic, ANIMATION_GROUPS_TYPE animType)
        {
            switch (animType)
            {
                case ANIMATION_GROUPS_TYPE.ANIMAL:
                    return ANIMATION_GROUPS.AG_LOW;

                case ANIMATION_GROUPS_TYPE.MONSTER:
                case ANIMATION_GROUPS_TYPE.SEA_MONSTER:
                    return ANIMATION_GROUPS.AG_HIGHT;

                case ANIMATION_GROUPS_TYPE.HUMAN:
                case ANIMATION_GROUPS_TYPE.EQUIPMENT:
                    return ANIMATION_GROUPS.AG_PEOPLE;
            }

            return ANIMATION_GROUPS.AG_HIGHT;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte GetDeathAction(
            ushort animID,
            ANIMATION_FLAGS animFlags,
            ANIMATION_GROUPS_TYPE animType,
            bool second,
            bool isRunning = false
        )
        {
            //ConvertBodyIfNeeded(ref animID);

            switch (animType)
            {
                case ANIMATION_GROUPS_TYPE.ANIMAL:

                    if (
                        (animFlags & ANIMATION_FLAGS.AF_USE_2_IF_HITTED_WHILE_RUNNING) != 0
                        || (animFlags & ANIMATION_FLAGS.AF_CAN_FLYING) != 0
                    )
                    {
                        return 2;
                    }

                    if ((animFlags & ANIMATION_FLAGS.AF_USE_UOP_ANIMATION) != 0)
                    {
                        return (byte)(second ? 3 : 2);
                    }

                    return (byte)(
                        second ? LOW_ANIMATION_GROUP.LAG_DIE_2 : LOW_ANIMATION_GROUP.LAG_DIE_1
                    );

                case ANIMATION_GROUPS_TYPE.SEA_MONSTER:
                {
                    if (!isRunning)
                    {
                        return 8;
                    }

                    goto case ANIMATION_GROUPS_TYPE.MONSTER;
                }

                case ANIMATION_GROUPS_TYPE.MONSTER:

                    if ((animFlags & ANIMATION_FLAGS.AF_USE_UOP_ANIMATION) != 0)
                    {
                        return (byte)(second ? 3 : 2);
                    }

                    return (byte)(
                        second ? HIGHT_ANIMATION_GROUP.HAG_DIE_2 : HIGHT_ANIMATION_GROUP.HAG_DIE_1
                    );

                case ANIMATION_GROUPS_TYPE.HUMAN:
                case ANIMATION_GROUPS_TYPE.EQUIPMENT:
                    return (byte)(
                        second ? PEOPLE_ANIMATION_GROUP.PAG_DIE_2 : PEOPLE_ANIMATION_GROUP.PAG_DIE_1
                    );
            }

            return 0;
        }

        public Span<FrameInfo> ReadUOPAnimationFrames(
            ushort animID,
            byte animGroup,
            byte direction,
            ANIMATION_GROUPS_TYPE type,
            int fileIndex
        )
        {
            if (fileIndex < 0 || fileIndex >= _filesUop.Length)
            {
                return Span<FrameInfo>.Empty;
            }

            var file = _filesUop[fileIndex];
            ref readonly var index = ref GetValidRefEntry(animID);

            if (index.Offset == 0 || (index.Offset + index.Length) >= index.Address.ToInt64())
            {
                return Span<FrameInfo>.Empty;
            }

            if (_frames == null)
            {
                _frames = new FrameInfo[22];
            }

            if (
                fileIndex == 0
                && index.Length == 0
                && index.DecompressedLength == 0
                && index.Offset == 0
            )
            {
                Log.Warn("uop animData is null");

                return Span<FrameInfo>.Empty;
            }

            file.Seek(index.Offset);

            if (_decompressedData == null || index.DecompressedLength > _decompressedData.Length)
            {
                _decompressedData = new byte[index.DecompressedLength];
            }

            fixed (byte* ptr = _decompressedData.AsSpan())
            {
                ZLib.Decompress(
                    file.PositionAddress,
                    index.Length,
                    0,
                    (IntPtr)ptr,
                    index.DecompressedLength
                );
            }

            var reader = new StackDataReader(
                _decompressedData.AsSpan().Slice(0, index.DecompressedLength)
            );
            reader.Skip(32);

            long end = (long)reader.StartAddress + reader.Length;

            int fc = reader.ReadInt32LE();
            uint dataStart = reader.ReadUInt32LE();
            reader.Seek(dataStart);

            byte frameCount = (byte)(
                type < ANIMATION_GROUPS_TYPE.EQUIPMENT ? Math.Round(fc / 5f) : 10
            );
            if (frameCount > _frames.Length)
            {
                _frames = new FrameInfo[frameCount];
            }

            var frames = _frames.AsSpan(0, frameCount);

            /* If the UOP files didn't omit frames, we could just do this:
             * reader.Skip(sizeof(UOPAnimationHeader) * direction * frameCount);
             * but we can't. So we have to walk through the frames to seek to where we need to go.
             */
            UOPAnimationHeader* animHeaderInfo = (UOPAnimationHeader*)reader.PositionAddress;

            for (ushort currentDir = 0; currentDir <= direction; currentDir++)
            {
                for (ushort frameNum = 0; frameNum < frameCount; frameNum++)
                {
                    long start = reader.Position;
                    animHeaderInfo = (UOPAnimationHeader*)reader.PositionAddress;

                    if (animHeaderInfo->Group != animGroup)
                    {
                        /* Something bad has happened. Just return. */
                        return Span<FrameInfo>.Empty;
                    }

                    /* FrameID is 1's based and just keeps increasing, regardless of direction.
                     * So north will be 1-22, northeast will be 23-44, etc. And it's possible for frames
                     * to be missing. */
                    ushort headerFrameNum = (ushort)((animHeaderInfo->FrameID - 1) % frameCount);

                    ref var frame = ref frames[frameNum];

                    // we need to zero-out the frame or we will see ghost animations coming from other animation queries
                    frame.Num = frameNum;
                    frame.CenterX = 0;
                    frame.CenterY = 0;
                    frame.Width = 0;
                    frame.Height = 0;

                    if (frameNum < headerFrameNum)
                    {
                        /* Missing frame. Keep walking forward. */
                        continue;
                    }

                    if (frameNum > headerFrameNum)
                    {
                        /* We've reached the next direction early */
                        break;
                    }

                    if (currentDir == direction)
                    {
                        /* We're on the direction we actually wanted to read */
                        if (start + animHeaderInfo->DataOffset >= reader.Length)
                        {
                            /* File seems to be corrupt? Skip loading. */
                            continue;
                        }

                        reader.Skip((int)animHeaderInfo->DataOffset);

                        ushort* palette = (ushort*)reader.PositionAddress;
                        reader.Skip(512);

                        ReadSpriteData(ref reader, palette, ref frame, true);
                    }

                    reader.Seek(start + sizeof(UOPAnimationHeader));
                }
            }

            reader.Release();

            return frames;
        }

        public Span<FrameInfo> ReadMULAnimationFrames(int fileIndex, AnimIdxBlock index)
        {
            if (fileIndex < 0 || fileIndex >= _filesUop.Length)
            {
                return Span<FrameInfo>.Empty;
            }

           
            //var animIndices = LoadAnimIndex(ref fileIndex, ref animID);

            //var offset = action * MAX_DIRECTIONS + direction;
            //if (offset >= animIndices.Length)
            //    return Span<FrameInfo>.Empty;

            //var index = animIndices[action * MAX_DIRECTIONS + direction];

            if (index.Position == 0 && index.Size == 0)
            {
                return Span<FrameInfo>.Empty;
            }

            if (index.Position == 0xFFFF_FFFF) // TODO: Size != 0xFFFF_FFFF ?
            {
                return Span<FrameInfo>.Empty;
            }

            var file = _files[fileIndex];

            var reader = new StackDataReader(
                new ReadOnlySpan<byte>(
                    (byte*)file.StartAddress.ToPointer() + index.Position,
                    length: (int)index.Size
                )
            );
            reader.Seek(0);

            ushort* palette = (ushort*)reader.PositionAddress;
            reader.Skip(512);

            long dataStart = reader.Position;
            uint frameCount = reader.ReadUInt32LE();
            uint* frameOffset = (uint*)reader.PositionAddress;

            if (_frames == null || frameCount > _frames.Length)
            {
                _frames = new FrameInfo[frameCount];
            }

            var frames = _frames.AsSpan().Slice(0, (int)frameCount);

            for (int i = 0; i < frameCount; i++)
            {
                reader.Seek(dataStart + frameOffset[i]);

                frames[i].Num = i;
                ReadSpriteData(ref reader, palette, ref frames[i], false);
            }

            return frames;
        }

        private void ReadSpriteData(
            ref StackDataReader reader,
            ushort* palette,
            ref FrameInfo frame,
            bool alphaCheck
        )
        {
            frame.CenterX = reader.ReadInt16LE();
            frame.CenterY = reader.ReadInt16LE();
            frame.Width = reader.ReadInt16LE();
            frame.Height = reader.ReadInt16LE();

            if (frame.Width <= 0 || frame.Height <= 0)
            {
                return;
            }

            int bufferSize = frame.Width * frame.Height;

            if (frame.Pixels == null || frame.Pixels.Length < bufferSize)
            {
                frame.Pixels = new uint[bufferSize];
            }
            else
            {
                frame.Pixels.AsSpan().Slice(0, bufferSize).Fill(0);
            }

            Span<uint> data = frame.Pixels;

            uint header = reader.ReadUInt32LE();

            while (header != 0x7FFF7FFF && reader.Position < reader.Length)
            {
                ushort runLength = (ushort)(header & 0x0FFF);
                int x = (int)((header >> 22) & 0x03FF);

                if ((x & 0x0200) > 0)
                {
                    x |= unchecked((int)0xFFFFFE00);
                }

                int y = (int)((header >> 12) & 0x3FF);

                if ((y & 0x0200) > 0)
                {
                    y |= unchecked((int)0xFFFFFE00);
                }

                x += frame.CenterX;
                y += frame.CenterY + frame.Height;

                int block = y * frame.Width + x;

                for (int k = 0; k < runLength; ++k, ++block)
                {
                    ushort val = palette[reader.ReadUInt8()];

                    // FIXME: same of MUL ? Keep it as original for the moment
                    if (!alphaCheck || val != 0)
                    {
                        data[block] = HuesHelper.Color16To32(val) | 0xFF_00_00_00;
                    }
                    else
                    {
                        data[block] = 0;
                    }
                }

                header = reader.ReadUInt32LE();
            }
        }

        public struct FrameInfo
        {
            public int Num;
            public short CenterX;
            public short CenterY;
            public short Width;
            public short Height;
            public uint[] Pixels;
        }

        public struct SittingInfoData
        {
            public SittingInfoData(
                ushort graphic,
                sbyte d1,
                sbyte d2,
                sbyte d3,
                sbyte d4,
                sbyte offsetY,
                sbyte mirrorOffsetY,
                bool drawback
            )
            {
                Graphic = graphic;
                Direction1 = d1;
                Direction2 = d2;
                Direction3 = d3;
                Direction4 = d4;
                OffsetY = offsetY;
                MirrorOffsetY = mirrorOffsetY;
                DrawBack = drawback;
            }

            public readonly ushort Graphic;
            public readonly sbyte Direction1,
                Direction2,
                Direction3,
                Direction4;
            public readonly sbyte OffsetY,
                MirrorOffsetY;
            public readonly bool DrawBack;

            public static SittingInfoData Empty = new SittingInfoData();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct AnimIdxBlock
        {
            public uint Position;
            public uint Size;
            public uint Unknown;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        ref struct UOPAnimationHeader
        {
            public ushort Group;
            public ushort FrameID;

            public ushort Unk0;
            public ushort Unk1;
            public ushort Unk2;
            public ushort Unk3;

            public uint DataOffset;
        }
    }

    public enum ANIMATION_GROUPS
    {
        AG_NONE = 0,
        AG_LOW,
        AG_HIGHT,
        AG_PEOPLE
    }

    public enum ANIMATION_GROUPS_TYPE
    {
        MONSTER = 0,
        SEA_MONSTER,
        ANIMAL,
        HUMAN,
        EQUIPMENT,
        UNKNOWN
    }

    public enum HIGHT_ANIMATION_GROUP
    {
        HAG_WALK = 0,
        HAG_STAND,
        HAG_DIE_1,
        HAG_DIE_2,
        HAG_ATTACK_1,
        HAG_ATTACK_2,
        HAG_ATTACK_3,
        HAG_MISC_1,
        HAG_MISC_2,
        HAG_MISC_3,
        HAG_STUMBLE,
        HAG_SLAP_GROUND,
        HAG_CAST,
        HAG_GET_HIT_1,
        HAG_MISC_4,
        HAG_GET_HIT_2,
        HAG_GET_HIT_3,
        HAG_FIDGET_1,
        HAG_FIDGET_2,
        HAG_FLY,
        HAG_LAND,
        HAG_DIE_IN_FLIGHT,
        HAG_ANIMATION_COUNT
    }

    public enum PEOPLE_ANIMATION_GROUP
    {
        PAG_WALK_UNARMED = 0,
        PAG_WALK_ARMED,
        PAG_RUN_UNARMED,
        PAG_RUN_ARMED,
        PAG_STAND,
        PAG_FIDGET_1,
        PAG_FIDGET_2,
        PAG_STAND_ONEHANDED_ATTACK,
        PAG_STAND_TWOHANDED_ATTACK,
        PAG_ATTACK_ONEHANDED,
        PAG_ATTACK_UNARMED_1,
        PAG_ATTACK_UNARMED_2,
        PAG_ATTACK_TWOHANDED_DOWN,
        PAG_ATTACK_TWOHANDED_WIDE,
        PAG_ATTACK_TWOHANDED_JAB,
        PAG_WALK_WARMODE,
        PAG_CAST_DIRECTED,
        PAG_CAST_AREA,
        PAG_ATTACK_BOW,
        PAG_ATTACK_CROSSBOW,
        PAG_GET_HIT,
        PAG_DIE_1,
        PAG_DIE_2,
        PAG_ONMOUNT_RIDE_SLOW,
        PAG_ONMOUNT_RIDE_FAST,
        PAG_ONMOUNT_STAND,
        PAG_ONMOUNT_ATTACK,
        PAG_ONMOUNT_ATTACK_BOW,
        PAG_ONMOUNT_ATTACK_CROSSBOW,
        PAG_ONMOUNT_SLAP_HORSE,
        PAG_TURN,
        PAG_ATTACK_UNARMED_AND_WALK,
        PAG_EMOTE_BOW,
        PAG_EMOTE_SALUTE,
        PAG_FIDGET_3,
        PAG_ANIMATION_COUNT
    }

    public enum LOW_ANIMATION_GROUP
    {
        LAG_WALK = 0,
        LAG_RUN,
        LAG_STAND,
        LAG_EAT,
        LAG_UNKNOWN,
        LAG_ATTACK_1,
        LAG_ATTACK_2,
        LAG_ATTACK_3,
        LAG_DIE_1,
        LAG_FIDGET_1,
        LAG_FIDGET_2,
        LAG_LIE_DOWN,
        LAG_DIE_2,
        LAG_ANIMATION_COUNT
    }

    [Flags]
    public enum ANIMATION_FLAGS : uint
    {
        AF_NONE = 0x00000,
        AF_UNKNOWN_1 = 0x00001,
        AF_USE_2_IF_HITTED_WHILE_RUNNING = 0x00002,
        AF_IDLE_AT_8_FRAME = 0x00004,
        AF_CAN_FLYING = 0x00008,
        AF_UNKNOWN_10 = 0x00010,
        AF_CALCULATE_OFFSET_LOW_GROUP_EXTENDED = 0x00020,
        AF_CALCULATE_OFFSET_BY_LOW_GROUP = 0x00040,
        AF_UNKNOWN_80 = 0x00080,
        AF_UNKNOWN_100 = 0x00100,
        AF_UNKNOWN_200 = 0x00200,
        AF_CALCULATE_OFFSET_BY_PEOPLE_GROUP = 0x00400,
        AF_UNKNOWN_800 = 0x00800,
        AF_UNKNOWN_1000 = 0x01000,
        AF_UNKNOWN_2000 = 0x02000,
        AF_UNKNOWN_4000 = 0x04000,
        AF_UNKNOWN_8000 = 0x08000,
        AF_USE_UOP_ANIMATION = 0x10000,
        AF_UNKNOWN_20000 = 0x20000,
        AF_UNKNOWN_40000 = 0x40000,
        AF_UNKNOWN_80000 = 0x80000,
        AF_FOUND = 0x80000000
    }

    public struct EquipConvData : IEquatable<EquipConvData>
    {
        public EquipConvData(ushort graphic, ushort gump, ushort color)
        {
            Graphic = graphic;
            Gump = gump;
            Color = color;
        }

        public ushort Graphic;
        public ushort Gump;
        public ushort Color;

        public override int GetHashCode()
        {
            return (Graphic, Gump, Color).GetHashCode();
        }

        public override bool Equals(object obj)
        {
            return obj is EquipConvData v && Equals(v);
        }

        public bool Equals(EquipConvData other)
        {
            return (Graphic, Gump, Color) == (other.Graphic, other.Gump, other.Color);
        }
    }

    struct BodyInfo
    {
        public ushort Graphic;
        public ushort Hue;
    }

    struct BodyConvInfo
    {
        public int FileIndex;
        public ANIMATION_GROUPS_TYPE AnimType;
        public ushort Graphic;
        public ushort Hue;
    }

    unsafe struct UopInfo
    {
        private fixed int _replacedAnim[AnimationsLoader.MAX_ACTIONS];

        public Span<int> ReplacedAnimations
        {
            get
            {

                fixed (int* ptr = _replacedAnim)
                {
                    return new Span<int>(ptr, AnimationsLoader.MAX_ACTIONS);
                }
            }
        }

        public int HeightOffset;
    }

    [Flags]
    public enum BodyConvFlags
    {
        Anim1 = 0x1,
        Anim2 = 0x2,
        Anim3 = 0x4,
        Anim4 = 0x8,
        Anim5 = 0x10,
    }
}
