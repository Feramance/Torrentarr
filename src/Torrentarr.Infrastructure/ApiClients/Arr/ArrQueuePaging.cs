namespace Torrentarr.Infrastructure.ApiClients.Arr;

internal static class ArrQueuePaging
{
    internal static async Task<List<T>> ReadAllAsync<T>(Func<int, int, Task<(int Page, int PageSize, int Total, List<T> Records)>> fetch,
        Func<T, int> identity, CancellationToken ct)
    {
        var items = new List<T>();
        var seen = new HashSet<int>();
        int? total = null;
        var size = 1000;
        for (var page = 1; ; page++)
        {
            ct.ThrowIfCancellationRequested();
            var response = await fetch(page, size);
            if (response.Page != page || response.PageSize <= 0 || page > 1 && response.PageSize != size || response.Total < 0 || total.HasValue && response.Total != total
                || response.Records.Count > response.PageSize)
                throw new IOException("Arr queue pagination is inconsistent; preserving previous queue state.");
            total ??= response.Total;
            size = response.PageSize;
            foreach (var item in response.Records)
                if (!seen.Add(identity(item))) throw new IOException("Arr queue page repeated an identity; snapshot is incomplete.");
            items.AddRange(response.Records);
            if (items.Count == total) return items;
            if (items.Count > total || response.Records.Count == 0 || response.Records.Count < size)
                throw new IOException("Arr queue ended before its advertised total; snapshot is incomplete.");
        }
    }
}
