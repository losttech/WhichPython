namespace LostTech.WhichPython {
    using System.Collections.Generic;
    using System.Threading;

    static class Tools {
        public static IEnumerable<T> WithCancellation<T>(this IEnumerable<T> enumerable, CancellationToken cancellation) {
            foreach(var item in enumerable) {
                cancellation.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }
}
