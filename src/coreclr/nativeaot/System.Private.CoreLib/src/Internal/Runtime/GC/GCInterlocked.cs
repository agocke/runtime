// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
namespace Internal.Runtime.GC
{
    internal unsafe struct Interlocked
    {
        public static int Increment(ref int addend) => System.Threading.Interlocked.Increment(ref addend);

        public static long Increment(ref long addend) => System.Threading.Interlocked.Increment(ref addend);

        public static int Decrement(ref int addend) => System.Threading.Interlocked.Decrement(ref addend);

        public static long Decrement(ref long addend) => System.Threading.Interlocked.Decrement(ref addend);

        public static void And(ref int destination, int value)
        {
            int comparand;
            int exchange;
            do
            {
                comparand = destination;
                exchange = comparand & value;
            }
            while (System.Threading.Interlocked.CompareExchange(ref destination, exchange, comparand) != comparand);
        }

        public static void And(ref uint destination, uint value)
        {
            fixed (uint* destinationAsUInt = &destination)
            {
                int* destinationAsInt = (int*)destinationAsUInt;
                int comparand;
                int exchange;
                do
                {
                    comparand = *destinationAsInt;
                    exchange = comparand & (int)value;
                }
                while (System.Threading.Interlocked.CompareExchange(ref *destinationAsInt, exchange, comparand) != comparand);
            }
        }

        public static void Or(ref int destination, int value)
        {
            int comparand;
            int exchange;
            do
            {
                comparand = destination;
                exchange = comparand | value;
            }
            while (System.Threading.Interlocked.CompareExchange(ref destination, exchange, comparand) != comparand);
        }

        public static void Or(ref uint destination, uint value)
        {
            fixed (uint* destinationAsUInt = &destination)
            {
                int* destinationAsInt = (int*)destinationAsUInt;
                int comparand;
                int exchange;
                do
                {
                    comparand = *destinationAsInt;
                    exchange = comparand | (int)value;
                }
                while (System.Threading.Interlocked.CompareExchange(ref *destinationAsInt, exchange, comparand) != comparand);
            }
        }

        public static int Exchange(ref int destination, int value) => System.Threading.Interlocked.Exchange(ref destination, value);

        public static long Exchange(ref long destination, long value) => System.Threading.Interlocked.Exchange(ref destination, value);

        public static nint ExchangePointer(nint* destination, nint value)
        {
            if (IntPtr.Size == sizeof(long))
            {
                return (nint)System.Threading.Interlocked.Exchange(ref *(long*)destination, (long)value);
            }

            return (nint)System.Threading.Interlocked.Exchange(ref *(int*)destination, (int)value);
        }

        public static nint ExchangePointer(nint* destination, void* value)
        {
            return ExchangePointer(destination, (nint)value);
        }

        public static int ExchangeAdd(ref int addend, int value) => System.Threading.Interlocked.Add(ref addend, value) - value;

        public static long ExchangeAdd64(ref long addend, long value) => System.Threading.Interlocked.Add(ref addend, value) - value;

        public static nint ExchangeAddPtr(nint* addend, nint value)
        {
            if (IntPtr.Size == sizeof(long))
            {
                return (nint)(System.Threading.Interlocked.Add(ref *(long*)addend, (long)value) - (long)value);
            }

            return (nint)(System.Threading.Interlocked.Add(ref *(int*)addend, (int)value) - (int)value);
        }

        public static int CompareExchange(ref int destination, int exchange, int comparand) =>
            System.Threading.Interlocked.CompareExchange(ref destination, exchange, comparand);

        public static long CompareExchange(ref long destination, long exchange, long comparand) =>
            System.Threading.Interlocked.CompareExchange(ref destination, exchange, comparand);

        public static nint CompareExchangePointer(nint* destination, nint exchange, nint comparand) =>
            IntPtr.Size == sizeof(long)
                ? (nint)System.Threading.Interlocked.CompareExchange(ref *(long*)destination, (long)exchange, (long)comparand)
                : (nint)System.Threading.Interlocked.CompareExchange(ref *(int*)destination, (int)exchange, (int)comparand);

        public static nint CompareExchangePointer(nint* destination, void* exchange, void* comparand) =>
            CompareExchangePointer(destination, (nint)exchange, (nint)comparand);
    }
}
