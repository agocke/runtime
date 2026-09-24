// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Internal.Runtime.GC
{
    internal static unsafe partial class GCWksInitialization
    {
        private struct CompactArgs
        {
            public byte* lastPlug;
            public nint lastPlugRelocation;
            public byte* beforeLastPlug;
            public nuint currentCompactedBrick;
            public bool isShortened;
            public mark* pinnedPlugEntry;
            public bool copyCards;
            public bool checkGenerationNumber;
            public int sourceGenerationNumber;
        }

        private static void GcMemCopy(byte* destination, byte* source, nuint length, bool copyCards)
        {
            if (destination == source)
            {
                return;
            }

            byte* destinationWithHeader = destination - sizeof(ObjHeader);
            byte* sourceWithHeader = source - sizeof(ObjHeader);
            if (destinationWithHeader < sourceWithHeader)
            {
                for (nuint i = 0; i < length; i++)
                {
                    destinationWithHeader[i] = sourceWithHeader[i];
                }
            }
            else
            {
                nuint copyLength = length;
                while (copyLength != 0)
                {
                    copyLength--;
                    destinationWithHeader[copyLength] = sourceWithHeader[copyLength];
                }
            }

            CopyCardsForRange(destination, source, length, copyCards);
        }

        private static void CopyCardsForRange(byte* destination, byte* source, nuint length, bool copyCards)
        {
            if (s_cardTable is null || length == 0)
            {
                return;
            }

            nuint destinationCard = CardIndexForAddress(destination);
            nuint endCard = CardIndexForAddress(destination + (nint)length - 1);
            for (nuint card = destinationCard; card <= endCard; card++)
            {
                byte* cardStart = (byte*)(card * CardSize);
                byte* coveredStart = cardStart > destination ? cardStart : destination;
                byte* cardEnd = cardStart + CardSize;
                byte* coveredEnd = cardEnd < destination + (nint)length
                    ? cardEnd
                    : destination + (nint)length;
                bool fullCard = coveredStart == cardStart && coveredEnd == cardEnd;
                bool sourceSet = false;
                if (copyCards)
                {
                    byte* sourceStart = source + (coveredStart - destination);
                    byte* sourceEnd = sourceStart + (coveredEnd - coveredStart) - 1;
                    nuint firstSourceCard = CardIndexForAddress(sourceStart);
                    nuint lastSourceCard = CardIndexForAddress(sourceEnd);
                    sourceSet = (s_cardTable[firstSourceCard / CardWordWidth] &
                        (1u << (int)(firstSourceCard % CardWordWidth))) != 0 ||
                        (s_cardTable[lastSourceCard / CardWordWidth] &
                        (1u << (int)(lastSourceCard % CardWordWidth))) != 0;
                }

                uint mask = 1u << (int)(card % CardWordWidth);
                if (sourceSet)
                {
                    s_cardTable[card / CardWordWidth] |= mask;
                }
                else if (fullCard)
                {
                    s_cardTable[card / CardWordWidth] &= ~mask;
                }
            }
        }

        private static nuint CardIndexForAddress(byte* address)
        {
            return (nuint)address / CardSize;
        }

        private static void CompactPlug(
            byte* plug,
            nuint size,
            bool checkLastObject,
            CompactArgs* args)
        {
            byte* relocatedPlug = plug + args->lastPlugRelocation;
            if (checkLastObject)
            {
                size += (nuint)sizeof(gap_reloc_pair);
                mark* entry = args->pinnedPlugEntry;
                if (entry is null)
                {
                    FailFast();
                    return;
                }

                if (args->isShortened)
                {
                    if (!entry->HasPostPlugInfo())
                    {
                        FailFast();
                        return;
                    }

                    gap_reloc_pair savedPostPlug = entry->saved_post_plug;
                    entry->saved_post_plug = entry->saved_post_plug_reloc;
                    entry->saved_post_plug_reloc = savedPostPlug;
                }
                else
                {
                    if (!entry->HasPrePlugInfo())
                    {
                        FailFast();
                        return;
                    }

                    gap_reloc_pair savedPrePlug = entry->saved_pre_plug;
                    entry->saved_pre_plug = entry->saved_pre_plug_reloc;
                    entry->saved_pre_plug_reloc = savedPrePlug;
                }
            }

            FailFastAssert(GetNodeRelocationDistance(plug) == args->lastPlugRelocation);
            bool alreadyPadded = IsPlugPadded(plug);
            if (alreadyPadded)
            {
                ClearPlugPadded(plug);
            }

            nuint unusedArraySize = alreadyPadded
                ? GCEnvironment.AlignUp(MinObjectSize, (nuint)sizeof(void*))
                : 0;
            if ((((nuint)((plug_and_reloc*)plug)[-1].reloc) & 1) != 0)
            {
                unusedArraySize += CompactionSwitchAlignmentSize(alreadyPadded);
            }

            if (unusedArraySize != 0)
            {
                byte* unusedArray = relocatedPlug - (nint)unusedArraySize;
                FormatUnusedArray(unusedArray, unusedArraySize);
                FixBrickForCompactionPadding(unusedArray, relocatedPlug);
            }

            GcMemCopy(relocatedPlug, plug, size, args->copyCards);

            if (args->checkGenerationNumber)
            {
                int sourceGeneration = args->sourceGenerationNumber;
                if (sourceGeneration == -1)
                {
                    sourceGeneration = GetObjectGenerationNumber(plug);
                }

                int destinationGeneration = GetObjectGenerationNumber(relocatedPlug);
                if (sourceGeneration < destinationGeneration)
                {
                    generation* generationState = GetGeneration(destinationGeneration);
                    if (generationState is null)
                    {
                        FailFast();
                        return;
                    }

                    generationState->allocation_size += size;
                }
            }

            nuint compactedBrick = args->currentCompactedBrick;
            nuint relocatedBrick = GetBrickIndex(relocatedPlug);
            if (relocatedBrick != compactedBrick)
            {
                if (args->beforeLastPlug is not null &&
                    compactedBrick != nuint.MaxValue)
                {
                    SetBrick(
                        compactedBrick,
                        (nint)(args->beforeLastPlug - BrickAddress(compactedBrick)));
                }

                compactedBrick = relocatedBrick;
            }

            nuint endBrick = GetBrickIndex(relocatedPlug + size - 1);
            if (endBrick != compactedBrick)
            {
                SetBrick(compactedBrick, (nint)(relocatedPlug - BrickAddress(compactedBrick)));
                for (nuint brick = compactedBrick + 1; brick < endBrick; brick++)
                {
                    SetBrick(brick, -1);
                }

                args->beforeLastPlug = BrickAddress(endBrick) - 1;
                compactedBrick = endBrick;
            }
            else
            {
                args->beforeLastPlug = relocatedPlug;
            }

            args->currentCompactedBrick = compactedBrick;
            if (checkLastObject)
            {
                mark* entry = args->pinnedPlugEntry;
                if (entry is null)
                {
                    FailFast();
                    return;
                }

                if (args->isShortened)
                {
                    gap_reloc_pair savedPostPlug = entry->saved_post_plug;
                    entry->saved_post_plug = entry->saved_post_plug_reloc;
                    entry->saved_post_plug_reloc = savedPostPlug;
                }
                else
                {
                    gap_reloc_pair savedPrePlug = entry->saved_pre_plug;
                    entry->saved_pre_plug = entry->saved_pre_plug_reloc;
                    entry->saved_pre_plug_reloc = savedPrePlug;
                }
            }
        }

        private static nuint CompactionSwitchAlignmentSize(bool alreadyPadded)
        {
            nuint alignment = (nuint)sizeof(void*);
            return alreadyPadded
                ? alignment
                : GCEnvironment.AlignUp(MinObjectSize, alignment) | alignment;
        }

        private static void FixBrickForCompactionPadding(byte* padding, byte* relocatedPlug)
        {
            nuint paddingBrick = GetBrickIndex(padding);
            nuint limit = GetBrickIndex(relocatedPlug);
            SetBrick(paddingBrick, (nint)(padding - BrickAddress(paddingBrick)));
            for (nuint brick = paddingBrick + 1; brick < limit; brick++)
            {
                SetBrick(brick, (nint)paddingBrick - (nint)brick);
            }
        }

        private static void RecoverCompactionPinnedInfo()
        {
            ResetPinnedQueueBos();
            while (!PinnedPlugQueueEmpty())
            {
                mark* entry = OldestPin();
                if (entry is null)
                {
                    FailFast();
                    return;
                }

                if (entry->HasPrePlugInfo())
                {
                    if (entry->saved_pre_plug_info_reloc_start is null)
                    {
                        FailFast();
                        return;
                    }

                    *(gap_reloc_pair*)entry->saved_pre_plug_info_reloc_start =
                        entry->saved_pre_plug_reloc;
                }

                if (entry->HasPostPlugInfo())
                {
                    if (entry->saved_post_plug_info_start is null)
                    {
                        FailFast();
                        return;
                    }

                    *(gap_reloc_pair*)entry->saved_post_plug_info_start =
                        entry->saved_post_plug_reloc;
                }

                DequeuePinnedPlug();
            }
        }

        private static void CompactInBrick(byte* tree, CompactArgs* args)
        {
            if (tree is null)
            {
                FailFast();
                return;
            }

            nint leftNode = GetNodeLeftChild(tree);
            nint rightNode = GetNodeRightChild(tree);
            nint relocation = GetNodeRelocationDistance(tree);
            if (leftNode != 0)
            {
                CompactInBrick(tree + leftNode, args);
            }

            bool hasPrePlugInfo = false;
            bool hasPostPlugInfo = false;
            if (!PinnedPlugQueueEmpty() && tree == OldestPin()->first)
            {
                args->pinnedPlugEntry = OldestPin();
                if (args->pinnedPlugEntry is null)
                {
                    FailFast();
                    return;
                }

                hasPrePlugInfo = args->pinnedPlugEntry->HasPrePlugInfo();
                hasPostPlugInfo = args->pinnedPlugEntry->HasPostPlugInfo();
                DequeuePinnedPlug();
            }

            if (args->lastPlug is not null)
            {
                nuint gapSize = (nuint)GetNodeGapSize(tree);
                byte* lastPlugEnd = tree - (nint)gapSize;
                nuint lastPlugSize = (nuint)(lastPlugEnd - args->lastPlug);
                FailFastAssert((lastPlugSize & ((nuint)sizeof(void*) - 1)) == 0);
                bool checkLastObject = args->isShortened || hasPrePlugInfo;
                if (!checkLastObject)
                {
                    FailFastAssert(lastPlugSize >= GCEnvironment.AlignUp(MinObjectSize, (nuint)sizeof(void*)));
                }

                CompactPlug(args->lastPlug, lastPlugSize, checkLastObject, args);
            }
            else
            {
                FailFastAssert(!hasPrePlugInfo);
            }

            args->lastPlug = tree;
            args->lastPlugRelocation = relocation;
            args->isShortened = hasPostPlugInfo;
            if (rightNode != 0)
            {
                CompactInBrick(tree + rightNode, args);
            }
        }

        private static int CompactPhase(int condemnedGeneration, byte* firstCondemnedAddress, bool clearCards)
        {
            if (condemnedGeneration < (int)gc_generation_num.soh_gen0 ||
                condemnedGeneration > (int)gc_generation_num.max_generation ||
                firstCondemnedAddress is null ||
                s_brickTable is null)
            {
                return E_NOTIMPL;
            }
            ResetPinnedQueueBos();

            int stopGeneration = GetStopGenerationIndex(condemnedGeneration);
            for (int generationNumber = condemnedGeneration;
                generationNumber >= stopGeneration;
                generationNumber--)
            {
                generation* generationState = GetGeneration(generationNumber);
                if (generationState is null)
                {
                    return E_FAIL;
                }

                heap_segment* segment = HeapSegmentRw(generationState->start_segment);
                if (segment is null || segment->allocated <= firstCondemnedAddress)
                {
                    continue;
                }

                if (segment->next is not null)
                {
                    return E_NOTIMPL;
                }

                nuint currentBrick = GetBrickIndex(firstCondemnedAddress);
                nuint endBrick = GetBrickIndex(segment->allocated - 1);
                CompactArgs args = default;
                args.currentCompactedBrick = nuint.MaxValue;
                args.copyCards = condemnedGeneration >= (int)gc_generation_num.soh_gen1 || !clearCards;
                args.checkGenerationNumber = false;
                while (currentBrick <= endBrick)
                {
                    int brickEntry = s_brickTable[currentBrick];
                    if (brickEntry > 0)
                    {
                        CompactInBrick(BrickAddress(currentBrick) + brickEntry - 1, &args);
                    }

                    currentBrick++;
                }

                if (args.lastPlug is not null)
                {
                    CompactPlug(
                        args.lastPlug,
                        (nuint)(segment->allocated - args.lastPlug),
                        args.isShortened,
                        &args);
                }

                if (args.beforeLastPlug is not null &&
                    args.currentCompactedBrick != nuint.MaxValue)
                {
                    SetBrick(
                        args.currentCompactedBrick,
                        (nint)(args.beforeLastPlug - BrickAddress(args.currentCompactedBrick)));
                }
            }

            RecoverCompactionPinnedInfo();
            return S_OK;
        }

    }
}
