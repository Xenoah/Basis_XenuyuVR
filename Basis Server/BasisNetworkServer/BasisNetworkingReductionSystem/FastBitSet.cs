using System;
using System.Threading;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public partial class BasisServerReductionSystemEvents
    {
        /// <summary>
        /// 32-bit word を backing store にする bitset。通常 operation は lock-free。
        /// - bit ごとの Set/Clear は int word に対する CAS を使う。
        /// - read は Volatile.Read を使う。
        /// - resize は lock で保護する (稀な path)。
        /// Length は addressable bit 数 (EnsureCapacity 後は highest set index + 1 以上)。
        /// </summary>
        public sealed class FastBitSet
        {
            private const int BitsPerElement = 32;

            // backing storage (各 int は 32 bits)。
            private int[] _words;

            // array resize だけを保護する (稀な path)。
            private readonly object _resizeLock = new();

            /// <summary>resize なしで address できる bit 数。</summary>
            public int Length { get; private set; }

            public FastBitSet(int initialBitCount)
            {
                if (initialBitCount < 0) throw new ArgumentOutOfRangeException(nameof(initialBitCount));
                var wordCount = (initialBitCount + BitsPerElement - 1) / BitsPerElement;
                _words = new int[wordCount];
                Length = wordCount * BitsPerElement;
            }

            /// <summary>
            /// <paramref name="index"/> の bit を address できるようにする。
            /// 必要なら underlying array を resize する (synchronized)。
            /// </summary>
            private void EnsureCapacity(int index)
            {
                if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
                if (index < Length) return;

                lock (_resizeLock)
                {
                    if (index < Length) return;

                    int requiredBits = index + 1;
                    int requiredWords = (requiredBits + BitsPerElement - 1) / BitsPerElement;

                    if (requiredWords > _words.Length)
                    {
                        // 将来の resize を減らすため、1.5 倍に grow する。
                        int newWords = Math.Max(requiredWords, _words.Length + (_words.Length >> 1) + 1);
                        var newArr = new int[newWords];
                        Array.Copy(_words, newArr, _words.Length);
                        _words = newArr;
                    }

                    Length = _words.Length * BitsPerElement;
                }
            }

            /// <summary>
            /// <paramref name="index"/> の bit を atomic に set / clear する。
            /// </summary>
            public void Set(int index, bool value)
            {
                EnsureCapacity(index);

                int elem = index / BitsPerElement;
                int bit = index % BitsPerElement;

                uint mask = 1U << bit;
                ref int wordRef = ref _words[elem];

                while (true)
                {
                    int oldVal = Volatile.Read(ref wordRef);
                    uint uOld = unchecked((uint)oldVal);
                    uint uNew = value ? (uOld | mask) : (uOld & ~mask);

                    // 変化がない場合は完了。不要な CAS を避ける。
                    if (uNew == uOld) return;

                    int newVal = unchecked((int)uNew);
                    if (Interlocked.CompareExchange(ref wordRef, newVal, oldVal) == oldVal)
                        return; // success
                    // それ以外は race に負けたので retry する。
                }
            }

            /// <summary>
            /// bit を atomic に test し、set されていれば clear する。
            /// 以前に bit が set されていた場合だけ true を返す。
            /// </summary>
            public bool TestAndClear(int index)
            {
                EnsureCapacity(index);

                int elem = index / BitsPerElement;
                int bit = index % BitsPerElement;

                uint mask = 1U << bit;
                ref int wordRef = ref _words[elem];

                while (true)
                {
                    int oldVal = Volatile.Read(ref wordRef);
                    uint uOld = unchecked((uint)oldVal);

                    if ((uOld & mask) == 0)
                        return false; // already clear; no write needed

                    uint uNew = uOld & ~mask;
                    int newVal = unchecked((int)uNew);

                    if (Interlocked.CompareExchange(ref wordRef, newVal, oldVal) == oldVal)
                        return true; // we cleared it
                    // それ以外は retry する。
                }
            }

            /// <summary>
            /// <paramref name="index"/> の bit が set されている場合 true を返す。
            /// </summary>
            public bool Get(int index)
            {
                if (index < 0) return false;
                if (index >= Length) return false;

                int elem = index / BitsPerElement;
                int bit = index % BitsPerElement;

                uint mask = 1U << bit;
                uint word = unchecked((uint)Volatile.Read(ref _words[elem]));
                return (word & mask) != 0;
            }

            /// <summary>
            /// addressable bit すべてを指定値に設定する。
            /// resize lock 下で bulk write する (単純で安全)。
            /// </summary>
            public void SetAll(bool value)
            {
                int fill = value ? unchecked((int)0xFFFFFFFFu) : 0;
                lock (_resizeLock)
                {
                    for (int i = 0; i < _words.Length; i++)
                        _words[i] = fill;
                }
            }

            /// <summary>すべての bit を clear する。</summary>
            public void Clear()
            {
                lock (_resizeLock)
                {
                    Array.Clear(_words, 0, _words.Length);
                }
            }

            /// <summary>いずれかの bit が set されている場合 true を返す。</summary>
            public bool AnyTrue()
            {
                // resize lock なしでも安全。最悪の場合は concurrent grow を見逃すが、
                // その場合でも new word は zero-initialized されている。
                for (int i = 0; i < _words.Length; i++)
                {
                    if (Volatile.Read(ref _words[i]) != 0)
                        return true;
                }
                return false;
            }
        }
    }
}
