﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CompatBot.Utils;
using Microsoft.EntityFrameworkCore;

namespace CompatBot.Database.Providers
{
    using TSyscallStats = Dictionary<string, Dictionary<string, HashSet<string>>>;

    internal static class SyscallInfoProvider
    {
        private static readonly SemaphoreSlim Limiter = new SemaphoreSlim(1, 1);

        public static async Task SaveAsync(TSyscallStats syscallInfo)
        {
            if (syscallInfo == null || syscallInfo.Count == 0)
                return;

            if (await Limiter.WaitAsync(1000, Config.Cts.Token))
            {
                try
                {
                    using (var db = new ThumbnailDb())
                    {
                        foreach (var productCodeMap in syscallInfo)
                        {
                            var product = db.Thumbnail.AsNoTracking().FirstOrDefault(t => t.ProductCode == productCodeMap.Key)
                                          ?? db.Thumbnail.Add(new Thumbnail {ProductCode = productCodeMap.Key}).Entity;
                            foreach (var moduleMap in productCodeMap.Value)
                            foreach (var func in moduleMap.Value)
                            {
                                var syscall = db.SyscallInfo.AsNoTracking().FirstOrDefault(sci => sci.Module == moduleMap.Key.ToUtf8() && sci.Function == func.ToUtf8())
                                              ?? db.SyscallInfo.Add(new SyscallInfo {Module = moduleMap.Key.ToUtf8(), Function = func.ToUtf8() }).Entity;
                                if (!db.SyscallToProductMap.Any(m => m.ProductId == product.Id && m.SyscallInfoId == syscall.Id))
                                    db.SyscallToProductMap.Add(new SyscallToProductMap {ProductId = product.Id, SyscallInfoId = syscall.Id});
                            }
                        }
                        await db.SaveChangesAsync(Config.Cts.Token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Limiter.Release();
                }
            }
        }

        public static async Task<(int funcs, int links)> FixInvalidFunctionNamesAsync()
        {
            var syscallStats = new TSyscallStats();
            int funcs = 0, links = 0;
            using (var db = new ThumbnailDb())
            {
                var funcsToFix = new List<SyscallInfo>(0);
                try
                {
                    funcsToFix = await db.SyscallInfo.Where(sci => sci.Function.Contains('(')).ToListAsync().ConfigureAwait(false);
                    funcs = funcsToFix.Count;
                    if (funcs == 0)
                        return (0, 0);

                    foreach (var sci in funcsToFix)
                    {
                        var productIds = await db.SyscallToProductMap.AsNoTracking().Where(m => m.SyscallInfoId == sci.Id).Select(m => m.Product.ProductCode).Distinct().ToListAsync().ConfigureAwait(false);
                        links += productIds.Count;
                        foreach (var productId in productIds)
                        {
                            if (!syscallStats.TryGetValue(productId, out var scInfo))
                                syscallStats[productId] = scInfo = new Dictionary<string, HashSet<string>>();
                            if (!scInfo.TryGetValue(sci.Module, out var smInfo))
                                scInfo[sci.Module] = smInfo = new HashSet<string>();
                            smInfo.Add(sci.Function.Split('(', 2)[0]);
                        }
                    }
                }
                catch (Exception e)
                {
                    Config.Log.Warn(e, "Failed to build fixed syscall mappings");
                    throw e;
                }
                await SaveAsync(syscallStats).ConfigureAwait(false);
                if (await Limiter.WaitAsync(1000, Config.Cts.Token))
                {
                    try
                    {
                        db.SyscallInfo.RemoveRange(funcsToFix);
                        await db.SaveChangesAsync().ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Config.Log.Warn(e, "Failed to remove broken syscall mappings");
                        throw e;
                    }
                    finally
                    {
                        Limiter.Release();
                    }
                }
            }
            return (funcs, links);
        }

        public static async Task<(int funcs, int links)> FixDuplicatesAsync()
        {
            int funcs = 0, links = 0;
            using (var db = new ThumbnailDb())
            {
                var duplicateFunctionNames = await db.SyscallInfo.Where(sci => db.SyscallInfo.Count(isci => isci.Function == sci.Function && isci.Module == sci.Module) > 1).Distinct().ToListAsync().ConfigureAwait(false);
                if (duplicateFunctionNames.Count == 0)
                    return (0, 0);

                if (await Limiter.WaitAsync(1000, Config.Cts.Token))
                {
                    try
                    {
                        foreach (var dupFunc in duplicateFunctionNames)
                        {
                            var dups = db.SyscallInfo.Where(sci => sci.Function == dupFunc.Function && sci.Module == dupFunc.Module).ToList();
                            if (dups.Count < 2)
                                continue;

                            var mostCommonDup = dups.Select(dup => (dup, count: db.SyscallToProductMap.Count(scm => scm.SyscallInfoId == dup.Id))).OrderByDescending(stat => stat.count).First().dup;
                            var dupsToRemove = dups.Where(df => df.Id != mostCommonDup.Id).ToList();
                            funcs += dupsToRemove.Count;
                            foreach (var dupToRemove in dupsToRemove)
                            {
                                var mappings = db.SyscallToProductMap.Where(scm => scm.SyscallInfoId == dupToRemove.Id).ToList();
                                links += mappings.Count;
                                foreach (var mapping in mappings)
                                {
                                    if (!db.SyscallToProductMap.Any(scm => scm.ProductId == mapping.ProductId && scm.SyscallInfoId == mostCommonDup.Id))
                                        db.SyscallToProductMap.Add(new SyscallToProductMap {ProductId = mapping.ProductId, SyscallInfoId = mostCommonDup.Id});
                                }
                            }
                            await db.SaveChangesAsync().ConfigureAwait(false);
                            db.SyscallInfo.RemoveRange(dupsToRemove);
                            await db.SaveChangesAsync().ConfigureAwait(false);
                        }
                        await db.SaveChangesAsync().ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Config.Log.Warn(e, "Failed to remove duplicate syscall entries");
                        throw;
                    }
                    finally
                    {
                        Limiter.Release();
                    }
                }
            }
            return (funcs, links);
        }
    }
}
