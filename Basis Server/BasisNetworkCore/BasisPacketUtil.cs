using System;
using System.Collections.Generic;
using System.Text;

namespace BasisNetworkCore
{
    public static class BasisPacketUtil
    {
        public static bool ValidatePacket(byte New, byte Old)
        {
            if (IsNewer(New, Old) && New != Old)
            {
                return true;
            }
            return false;
        }
        // seq1 が seq2 より新しい場合 true を返す。
        public static bool IsNewer(byte seq1, byte seq2)
        {
            return (byte)(seq1 - seq2) < 128;
        }
    }
}
