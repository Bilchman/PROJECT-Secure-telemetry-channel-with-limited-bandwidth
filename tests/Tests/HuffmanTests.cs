using System;
using System.Linq;
using System.Text;
using Xunit;
using Core;

namespace Tests
{
    public class HuffmanTests
    {

        //Еталонний кадр (ref_01_single_frame)
        [Fact]
        public void Test_Case1_Section7_Reference_Single_Frame_Exact_Match()
        {
            // Arrange: еталонний кадр телеметрії
            // 00 00 00 00 03 E8 00 D7 FF FD 01 01 (12 байтів)
            var compressor = new Huffman();
            byte[] frame = new byte[] { 
                0x00, 0x00, 0x00, 0x00, 0x03, 0xE8, 0x00, 0xD7, 0xFF, 0xFD, 0x01, 0x01 
            };
            byte[] expectedBitstream = new byte[] { 0x0C, 0xE6, 0xDF, 0x90 };


            var table = compressor.BuildTable(frame);

            // Assert: Перевірка довжин
            // 00 -> 1; 01, FF -> 3; 03, D7, E8, FD -> 4
            Assert.Equal(7, table.Entries.Count);
            Assert.Equal(1, table.Entries.First(e => e.Symbol == 0x00).Length);
            Assert.Equal(3, table.Entries.First(e => e.Symbol == 0x01).Length);
            Assert.Equal(3, table.Entries.First(e => e.Symbol == 0xFF).Length);
            Assert.Equal(4, table.Entries.First(e => e.Symbol == 0x03).Length);
            Assert.Equal(4, table.Entries.First(e => e.Symbol == 0xD7).Length);
            Assert.Equal(4, table.Entries.First(e => e.Symbol == 0xE8).Length);
            Assert.Equal(4, table.Entries.First(e => e.Symbol == 0xFD).Length);

            // Перевірка бітового потоку: має бути строго 0C E6 DF 90 (4 байти)
            var compressed = compressor.Compress(frame, table);
            Assert.Equal(expectedBitstream, compressed);

            // Побайтове відновлення
            var decompressed = compressor.Decompress(compressed, table, frame.Length);
            Assert.Equal(frame, decompressed);
        }

        // Порожній потік (ref_02_empty)
        [Fact]
        public void Test_Case2_Empty_Stream_Does_Not_Crash()
        {
            var compressor = new Huffman();
            byte[] empty = Array.Empty<byte>();

            var table = compressor.BuildTable(empty);
            Assert.Empty(table.Entries);

            var compressed = compressor.Compress(empty, table);
            Assert.Empty(compressed);

            var decompressed = compressor.Decompress(compressed, table, 0);
            Assert.Empty(decompressed);
        }

        //Канонічні коди та лексикографічне правило
        [Fact]
        public void Test_Case6_Canonical_Code_Assignment_Rule()
        {
            // при однаковій довжині символи з меншим числовим значенням
            // отримують чисельно менший код (0x01 отримує 100, 0xFF отримує 101)
            var compressor = new Huffman();
            byte[] frame = new byte[] { 
                0x00, 0x00, 0x00, 0x00, 0x03, 0xE8, 0x00, 0xD7, 0xFF, 0xFD, 0x01, 0x01 
            };

            var table = compressor.BuildTable(frame);
            
            // Якщо канонізація відпрацює некоректно, стиснутий потік не зійдеться з еталоном
            var compressed = compressor.Compress(frame, table);
            Assert.Equal(new byte[] { 0x0C, 0xE6, 0xDF, 0x90 }, compressed);

            var decompressed = compressor.Decompress(compressed, table, frame.Length);
            Assert.Equal(frame, decompressed);
        }

        //Вироджений алфавіт — 1 символ (ref_03_degenerate)
        [Fact]
        public void Test_Case7_Degenerate_Alphabet_Single_Symbol()
        {
            // Вимога §3.2: 1 символ -> length = 1, code = 0
            var compressor = new Huffman();
            byte[] data = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA }; // 8 однакових байтів

            var table = compressor.BuildTable(data);

            Assert.Single(table.Entries);
            Assert.Equal(0xAA, table.Entries[0].Symbol);
            Assert.Equal(1, table.Entries[0].Length);

            // 8 символів по 1 біту '0' дають бітовий потік "00000000" = 1 байт 0x00
            var compressed = compressor.Compress(data, table);
            Assert.Single(compressed);
            Assert.Equal(0x00, compressed[0]);

            var decompressed = compressor.Decompress(compressed, table, data.Length);
            Assert.Equal(data, decompressed);
        }

        // Вироджений алфавіт — усі символи рівноймовірні
        [Fact]
        public void Test_C2_2_Equiprobable_All_256_Symbols()
        {
            var compressor = new Huffman();
            byte[] data = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                data[i] = (byte)i; // кожен символ зустрічається рівно 1 раз
            }

            var table = compressor.BuildTable(data);

            Assert.Equal(256, table.Entries.Count);
            // У збалансованому дереві з 256 елементів глибина кожного має бути 8 біт
            Assert.All(table.Entries, e => Assert.Equal(8, e.Length));

            var compressed = compressor.Compress(data, table);
            var decompressed = compressor.Decompress(compressed, table, data.Length);
            Assert.Equal(data, decompressed);
        }

        //Свідома відмова від стиснення (ref_05_no_compression)
        [Fact]
        public void Test_C2_5_No_Compression_Mode()
        {
            var noCompress = new NoCompression();
            byte[] data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x42 };

            var table = noCompress.BuildTable(data);
            Assert.Empty(table.Entries); // У режимі NO_COMPRESSION table_nsym = 0

            var compressed = noCompress.Compress(data, table);
            Assert.Equal(data, compressed); // Байти передаються без змін

            var decompressed = noCompress.Decompress(compressed, table, data.Length);
            Assert.Equal(data, decompressed);
        }

        // Таблиця довжин — сортування за зростанням символу
        [Fact]
        public void Test_Table_Entries_Must_Be_Sorted_By_Symbol_Ascending()
        {
            var compressor = new Huffman();
            byte[] data = new byte[] { 0xFA, 0x05, 0x12, 0x88, 0x01, 0xFA, 0x05 };

            var table = compressor.BuildTable(data);

            Assert.True(table.Entries.Count > 1);
            for (int i = 0; i < table.Entries.Count - 1; i++)
            {
                Assert.True(table.Entries[i].Symbol < table.Entries[i + 1].Symbol,
                    $"Порушено сортування: символ {table.Entries[i].Symbol} повинен бути меншим за {table.Entries[i + 1].Symbol}");
            }
        }

        //Максимальна довжина коду не перевищує 15 біт
        [Fact]
        public void Test_Code_Lengths_Never_Exceed_15_Bits()
        {
            var compressor = new Huffman();
            byte[] data = Encoding.UTF8.GetBytes("Telemetry test stream with arbitrary text to build a full tree");

            var table = compressor.BuildTable(data);

            Assert.All(table.Entries, e => Assert.InRange(e.Length, 1, 15));
        }

        // Зупинка на symbol_count та ігнорування нулів доповнення
        [Fact]
        public void Test_Decompression_Stops_At_Exact_Symbol_Count()
        {
            var compressor = new Huffman();
            byte[] frame = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x03, 0xE8, 0x00, 0xD7, 0xFF, 0xFD, 0x01, 0x01 };
            
            var table = compressor.BuildTable(frame);
            var compressed = compressor.Compress(frame, table);

            // Декодер повинен прочитати рівно 12 байтів і зупинитися, не намагаючись розпарсити 32-й біт.
            var decompressed = compressor.Decompress(compressed, table, frame.Length);
            Assert.Equal(frame.Length, decompressed.Length);
            Assert.Equal(frame, decompressed);
        }

        // РОЗДІЛ 6: Відмова E_CODE при обриві потоку (Truncated stream)
        [Fact]
        public void Test_Failure_E_CODE_When_Stream_Is_Truncated()
        {
            var compressor = new Huffman();
            byte[] frame = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x03, 0xE8, 0x00, 0xD7, 0xFF, 0xFD, 0x01, 0x01 };

            var table = compressor.BuildTable(frame);
            var compressed = compressor.Compress(frame, table);

            // Обрізаємо бітовий потік (беремо 2 байти замість 4)
            byte[] truncated = compressed.Take(2).ToArray();

            // Декодер очікує 12 символів, але потік закінчиться раніше -> E_CODE
            var ex = Assert.Throws<InvalidOperationException>(() =>
                compressor.Decompress(truncated, table, frame.Length));

            Assert.Contains("E_CODE", ex.Message);
        }

        // Помилка при компресії символу, відсутнього в таблиці
        [Fact]
        public void Test_Failure_When_Compressing_Unknown_Symbol()
        {
            var compressor = new Huffman();
            byte[] trainData = new byte[] { 0x01, 0x02, 0x03 };
            var table = compressor.BuildTable(trainData);

            byte[] corruptedData = new byte[] { 0x01, 0x99 }; // 0x99 немає в таблиці

            Assert.Throws<InvalidOperationException>(() =>
                compressor.Compress(corruptedData, table));
        }

        // Базовий roundtrip-тест
        [Fact]
        public void Test_Compression_And_Decompression_Basic()
        {
            var compressor = new Huffman();
            byte[] data = Encoding.UTF8.GetBytes("telemetry data test frame 12345");

            var table = compressor.BuildTable(data);
            var compressed = compressor.Compress(data, table);
            var decompressed = compressor.Decompress(compressed, table, data.Length);

            Assert.Equal(data, decompressed);
        }
    }
}