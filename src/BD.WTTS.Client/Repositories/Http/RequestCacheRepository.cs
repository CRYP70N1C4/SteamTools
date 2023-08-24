using Fusillade;
using LiteDB;

// ReSharper disable once CheckNamespace
namespace BD.WTTS.Repositories;

internal sealed class RequestCacheRepository : IRequestCacheRepository, IDisposable
{
    const string DefaultRequestUri = "/";
    const string CacheDirName = "Http";
    const string TableName = "Fusillade_RequestCache";
    const string DbFileName = "RequestCache.LiteDB";

    readonly LiteDatabase db;
    readonly ILiteCollection<RequestCache> table;
    bool disposedValue;

    public RequestCacheRepository()
    {
        var dbPath = Path.Combine(IOPath.CacheDirectory, CacheDirName);
        IOPath.DirCreateByNotExists(dbPath);
        dbPath = Path.Combine(dbPath, DbFileName);
        db = new LiteDatabase(dbPath);
        BsonMapper.Global.Entity<RequestCache>().Id(x => x.Id, autoId: false);
        table = db.GetCollection<RequestCache>(TableName);
    }

    async Task IRequestCache.Save(
        HttpRequestMessage request,
        HttpResponseMessage response,
        string key,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            return; // 仅缓存成功的状态码

        if (request.Headers.TryGetValues(ApiConstants.Headers.Request.SecurityKey, out var _) ||
            request.Headers.TryGetValues(ApiConstants.Headers.Request.SecurityKeyHex, out var _))
        {
            // 加密的数据不进行缓存
            return;
        }

        var rspContent = response.Content;
        if (rspContent == null)
            return;

        var bytes = await rspContent.ReadAsByteArrayAsync(cancellationToken);
        if (!bytes.Any_Nullable())
            return;

        var requestUri = response.RequestMessage?.RequestUri ?? request.RequestUri;
        string requestUriString;
        if (requestUri == null)
        {
            requestUriString = DefaultRequestUri;
        }
        else
        {
            requestUriString = requestUri.ToString();
        }

        var fileTypeResult = FileFormat.AnalyzeFileType(bytes);
        var hashKey = Hashs.String.SHA384(bytes, false);
        var fileEx = fileTypeResult.FileEx;
        var fileName = hashKey + fileEx;
        var subDirName = fileTypeResult.ImageFormat.HasValue ? "Images" : "Binaries";
        var hash_0 = hashKey[..2];
        var hash_1 = hashKey[2..4]; // 通过两级目录来避免文件夹内文件过多造成的性能下降
        var relativePath = Path.Combine(CacheDirName, subDirName, hash_0, hash_1, fileName);
        var baseDirPath = Path.Combine(IOPath.CacheDirectory, CacheDirName, subDirName, hash_0, hash_1);
        IOPath.DirCreateByNotExists(baseDirPath);
        var filePath = Path.Combine(baseDirPath, fileName);
        var isWriteFile = true;
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Exists)
            {
                if (isWriteFile = fileInfo.Length != bytes.Length)
                {
                    fileInfo.Delete();
                }
            }
        }
        catch
        {

        }
        if (isWriteFile)
        {
            try
            {
                await File.WriteAllBytesAsync(filePath, bytes, cancellationToken); // 写入文件
            }
            catch
            {
                return;
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        IOPath.FileIfExistsItDelete(filePath);
                    }
                    catch
                    {

                    }
                }
            }
        }

        if (cancellationToken.IsCancellationRequested)
            return;

        var entity = table.FindById(key);
        bool isInsertOrUpdate;
        if (entity == null)
        {
            isInsertOrUpdate = true;
            entity = new()
            {
                Id = key,
            };
        }
        else
        {
            isInsertOrUpdate = false;
        }
        entity.HttpMethod = request.Method.Method;
        entity.RequestUri = requestUriString;
        entity.RelativePath = relativePath;

        if (cancellationToken.IsCancellationRequested)
            return;

        if (isInsertOrUpdate)
            table.Insert(entity);
        else
            table.Update(entity);
    }

    public Task UpdateUsageTimeByIdAsync(string id, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var entity = table.FindById(id);
        if (!cancellationToken.IsCancellationRequested && entity != null)
        {
            var now = DateTimeOffset.Now.Ticks;
            table.UpdateMany(x => new() { UsageTime = now, }, x => x.Id == id);
        }
    }, cancellationToken);

    async Task<byte[]> IRequestCache.Fetch(
        HttpRequestMessage request,
        string key,
        CancellationToken cancellationToken)
    {
        var requestUri = request.RequestUri;
        string requestUriString;
        if (requestUri == null)
        {
            requestUriString = DefaultRequestUri;
        }
        else
        {
            requestUriString = requestUri.ToString();
        }

        var entity = table.Query().Where(x => x.Id == key &&
            x.HttpMethod == request.Method.Method &&
            x.RequestUri == requestUriString).FirstOrDefault();

        if (!cancellationToken.IsCancellationRequested && entity != null)
        {
            try
            {
                var filePath = Path.Combine(IOPath.CacheDirectory, entity.RelativePath);
                if (File.Exists(filePath))
                {
                    var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
                    return bytes;
                }
            }
            catch
            {
                goto reZero;
            }
            await UpdateUsageTimeByIdAsync(entity.Id, cancellationToken);
        }

    reZero: return Array.Empty<byte>();
    }

    public Task<int> DeleteAllAsync() => Task.Run(() =>
    {
        var r = table.DeleteAll();
        var dirPath = Path.Combine(IOPath.CacheDirectory, CacheDirName);
        var items = Directory.EnumerateDirectories(dirPath);
        foreach (var item in items)
        {
            IOPath.DirTryDelete(item, true);
        }
        return r;
    });

    public Task<int> DeleteAllAsync(DateTimeOffset usageTime)
    {
        var usageTime_ = usageTime.Ticks;
        return Task.Run(() =>
        {
            Expression<Func<RequestCache, bool>> predicate = x => x.UsageTime < usageTime_;
            var items = table.Query().Where(predicate).ToArray();
            int r = items.Length;
            if (r > 0)
            {
                r = table.DeleteMany(predicate);
                foreach (var item in items)
                {
                    var filePath = Path.Combine(IOPath.CacheDirectory, item.RelativePath);
                    IOPath.FileTryDelete(filePath);
                }
            }
            return r;
        });
    }

    void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                // 释放托管状态(托管对象)
                db.Dispose();
            }

            // 释放未托管的资源(未托管的对象)并重写终结器
            // 将大型字段设置为 null
            disposedValue = true;
        }
    }

    public void Dispose()
    {
        // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}