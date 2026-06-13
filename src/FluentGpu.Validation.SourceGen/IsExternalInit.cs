// Polyfill: `init` accessors + positional records compile against netstandard2.0 (which lacks this type).
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
