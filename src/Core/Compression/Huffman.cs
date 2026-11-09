using System;
using System.Collections.Generic;

namespace Core
{
    public class Huffman : ICompressor
    {
        public string Name => "huffman";
        
        /// Побудова таблиці канонічних довжин кодів.
        public CodeTable BuildTable(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                return new CodeTable { Entries = Array.Empty<(byte, byte)>() };
            }

            // 1. Порахувати частоти байтів
            int[] originalFrequencies = new int[256];
            foreach (var b in data)
            {
                originalFrequencies[b]++;
            }

            int uniqueCount = 0;
            int singleSymbol = -1;

            for (int i = 0; i < 256; i++)
            {
                if (originalFrequencies[i] > 0)
                {
                    uniqueCount++;
                    singleSymbol = i;
                }
            }

            int[] codeLengths = new int[256];

            // Вироджений випадок: якщо всього 1 унікальний символ -> довжина 1 біт
            if (uniqueCount == 1)
            {
                codeLengths[singleSymbol] = 1;
            }
            else
            {
                // Ітеративне згладжування частот для дотримання обмеження <= 15 біт
                int[] workingFrequencies = (int[])originalFrequencies.Clone();

                while (true)
                {
                    Array.Clear(codeLengths, 0, codeLengths.Length);
                    BuildTreeAndCalculateDepths(workingFrequencies, codeLengths, out int maxDepth);

                    if (maxDepth <= 15)
                    {
                        break;
                    }

                    // Згладжування частот без видалення символів з алфавіту
                    for (int i = 0; i < 256; i++)
                    {
                        if (workingFrequencies[i] > 1)
                        {
                            workingFrequencies[i] = (workingFrequencies[i] + 1) / 2;
                        }
                    }
                }
            }

            // Збираємо записи (Symbol, Length), відсортовані за зростанням символу
            var entries = new List<(byte Symbol, byte Length)>();
            for (int i = 0; i < 256; i++)
            {
                if (codeLengths[i] > 0)
                {
                    entries.Add(((byte)i, (byte)codeLengths[i]));
                }
            }

            return new CodeTable { Entries = entries };
        }

        private void BuildTreeAndCalculateDepths(int[] frequencies, int[] codeLengths, out int maxDepth)
        {
            var pq = new PriorityQueue<HuffmanNode, HuffmanNode>();

            for (int i = 0; i < 256; i++)
            {
                if (frequencies[i] > 0)
                {
                    var node = new HuffmanNode
                    {
                        Symbol = i,
                        Frequency = frequencies[i],
                        MinSymbol = i
                    };
                    pq.Enqueue(node, node);
                }
            }

            while (pq.Count > 1)
            {
                var left = pq.Dequeue();
                var right = pq.Dequeue();

                var parent = new HuffmanNode
                {
                    Symbol = -1,
                    Frequency = left.Frequency + right.Frequency,
                    MinSymbol = Math.Min(left.MinSymbol, right.MinSymbol),
                    Left = left,
                    Right = right
                };
                pq.Enqueue(parent, parent);
            }

            var root = pq.Dequeue();
            maxDepth = 0;
            CalculateDepths(root, 0, codeLengths, ref maxDepth);
        }

        private void CalculateDepths(HuffmanNode? node, int depth, int[] codeLengths, ref int maxDepth)
        {
            if (node == null) return;

            if (node.IsLeaf)
            {
                codeLengths[node.Symbol] = depth;
                if (depth > maxDepth)
                {
                    maxDepth = depth;
                }
                return;
            }

            CalculateDepths(node.Left, depth + 1, codeLengths, ref maxDepth);
            CalculateDepths(node.Right, depth + 1, codeLengths, ref maxDepth);
        }

        public byte[] Compress(ReadOnlySpan<byte> data, CodeTable table)
        {
            if (data.IsEmpty)
            {
                return Array.Empty<byte>();
            }

            var writer = new BitWriter();
            var canonicalCodes = GenerateCanonicalCodes(table);

            foreach (var b in data)
            {
                if (!canonicalCodes.TryGetValue(b, out var codeInfo))
                {
                    throw new InvalidOperationException($"E_CODE: символ 0x{b:X2} відсутній у таблиці кодів.");
                }
                writer.WriteBits(codeInfo.Code, codeInfo.Length);
            }

            return writer.ToArray();
        }

        public byte[] Decompress(ReadOnlySpan<byte> bitstream, CodeTable table, int symbolCount)
        {
            if (symbolCount == 0)
            {
                return Array.Empty<byte>();
            }

            var reader = new BitReader(bitstream);
            var canonicalCodes = GenerateCanonicalCodes(table);

            // Створюємо зворотну мапу: (length, code) -> symbol
            var decodeMap = new Dictionary<(int length, uint code), byte>();
            foreach (var kvp in canonicalCodes)
            {
                decodeMap[(kvp.Value.Length, kvp.Value.Code)] = kvp.Key;
            }

            byte[] result = new byte[symbolCount];

            for (int i = 0; i < symbolCount; i++)
            {
                uint currentCode = 0;
                bool found = false;

                for (int currentLength = 1; currentLength <= 15; currentLength++)
                {
                    if (!reader.TryReadBit(out int bit))
                    {
                        break;
                    }

                    currentCode = (currentCode << 1) | (uint)bit;

                    if (decodeMap.TryGetValue((currentLength, currentCode), out byte symbol))
                    {
                        result[i] = symbol;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    throw new InvalidOperationException("E_CODE: потік обірвано або символ відсутній у таблиці.");
                }
            }

            return result;
        }


        private Dictionary<byte, (uint Code, int Length)> GenerateCanonicalCodes(CodeTable table)
        {
            int[] blCount = new int[16];
            foreach (var entry in table.Entries)
            {
                if (entry.Length < 1 || entry.Length > 15)
                {
                    throw new InvalidOperationException($"E_CODE: неприпустима довжина коду {entry.Length} у таблиці.");
                }
                blCount[entry.Length]++;
            }

            int[] nextCode = new int[16];
            int code = 0;
            blCount[0] = 0;

            for (int bits = 1; bits <= 15; bits++)
            {
                code = (code + blCount[bits - 1]) << 1;
                nextCode[bits] = code;
            }

            var result = new Dictionary<byte, (uint Code, int Length)>();

            var sortedEntries = new List<(byte Symbol, byte Length)>(table.Entries);
            sortedEntries.Sort((a, b) => a.Symbol.CompareTo(b.Symbol));

            foreach (var entry in sortedEntries)
            {
                int len = entry.Length;
                uint assignedCode = (uint)nextCode[len];
                nextCode[len]++;
                result[entry.Symbol] = (assignedCode, len);
            }

            return result;
        }
    }

    internal class HuffmanNode : IComparable<HuffmanNode>
    {
        public int Symbol { get; set; } = -1;
        public int Frequency { get; set; }
        public int MinSymbol { get; set; }
        public HuffmanNode? Left { get; set; }
        public HuffmanNode? Right { get; set; }

        public bool IsLeaf => Left == null && Right == null;

        public int CompareTo(HuffmanNode? other)
        {
            if (other == null) return 1;

            int cmp = Frequency.CompareTo(other.Frequency);
            if (cmp != 0) return cmp;

            return MinSymbol.CompareTo(other.MinSymbol);
        }
    }
}