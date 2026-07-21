using System;
using System.Collections.Generic;
using System.Text;

namespace Lyric.Core
{
    public sealed class InternalCompilationException(string message, Exception? inner = null) : Exception(message?.ToString(), inner);
    public static class InternalExceptions
    {
        public static void Unreachable(object value)
        {
            throw new InternalCompilationException($"unreachable: unexpected {value}");
        }
    }
}
