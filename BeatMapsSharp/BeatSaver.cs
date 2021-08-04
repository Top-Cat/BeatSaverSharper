﻿using BeatMapsSharp.Http;
using BeatMapsSharp.Models;
using BeatMapsSharp.Models.Pages;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BeatMapsSharp
{
    public class BeatSaver : IDisposable
    {
        internal bool IsDisposed { get; set; }
        private readonly IHttpService _httpService;
        private static readonly (string, PropertyInfo)[] _filterProperties;
        private readonly ConcurrentDictionary<int, User> _fetchedUsers = new ConcurrentDictionary<int, User>();
        private readonly ConcurrentDictionary<string, User> _fetchedUsernames = new ConcurrentDictionary<string, User>();
        private readonly ConcurrentDictionary<string, Beatmap> _fetchedBeatmaps = new ConcurrentDictionary<string, Beatmap>();
        private readonly ConcurrentDictionary<string, Beatmap> _fetchedHashedBeatmaps = new ConcurrentDictionary<string, Beatmap>();

        static BeatSaver()
        {
            // We cache the reflection properties and their names.
            var properties = typeof(SearchTextFilterOption).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            _filterProperties = new (string, PropertyInfo)[properties.Length];
            for (int i = 0; i < properties.Length; i++)
            {
                var activeProperty = properties[i];
                var queryKeyName = activeProperty.GetCustomAttribute<SearchTextFilterOption.QueryKeyNameAttribute>();
                _filterProperties[i] = (queryKeyName?.Name ?? activeProperty.Name, activeProperty);
            }
        }

        public BeatSaver()
        {
            _httpService = new HttpClientService("https://beatmaps.io/api/", TimeSpan.FromSeconds(30), "BeatSaverSharp/3.0.0");
        }

        #region Beatmaps

        public async Task<Beatmap?> Beatmap(string key, CancellationToken? token = null)
        {
            key = key.ToLowerInvariant();
            if (_fetchedBeatmaps.TryGetValue(key, out Beatmap? beatmap))
                return beatmap;

            return await FetchBeatmap("maps/id/" + key, token);
        }

        public async Task<Beatmap?> BeatmapByHash(string hash, CancellationToken? token = null)
        {
            hash = hash.ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(hash))
                return null;

            if (_fetchedHashedBeatmaps.TryGetValue(hash, out Beatmap? beatmap))
                return beatmap;

            return await FetchBeatmap("maps/hash/" + hash, token);
        }

        private async Task<Beatmap?> FetchBeatmap(string url, CancellationToken? token = null)
        {
            var response = await _httpService.GetAsync(url, token).ConfigureAwait(false);
            if (!response.Successful)
                return null;

            Beatmap beatmap = await response.ReadAsObjectAsync<Beatmap>();
            GetOrAddBeatmapToCache(beatmap, out beatmap);
            return beatmap;
        }

        #endregion

        #region Paged Beatmaps

        public async Task<Page?> UploadedBeatmaps(UploadedFilterOptions options = default, CancellationToken? token = null)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("maps/latest?automapper=");
            sb.Append(options.IncludeAutomappers);
            if (options.StartDate.HasValue)
                sb.Append("&after=").Append(options.StartDate.Value.ToString("O"));

            var result = await GetBeatmapsFromPage(sb.ToString(), token).ConfigureAwait(false);
            if (result is null)
                return null;

            return new UploadedPage(ref options, result)
            {
                Client = this
            };
        }

        public async Task<Page?> UploaderBeatmaps(int uploaderID, int page = 0, CancellationToken? token = null)
        {
            var result = await GetBeatmapsFromPage($"maps/uploader/{uploaderID}/{page}", token).ConfigureAwait(false);
            if (result is null)
                return null;

            return new UploaderPage(page, uploaderID, result)
            {
                Client = this
            };
        }

        public async Task<Page?> SearchBeatmaps(SearchTextFilterOption? searchOptions = default, int page = 0, CancellationToken? token = null)
        {
            string searchURL = $"search/text/{page}";

            if (searchOptions != null)
            {
                List<string> queryProps = new List<string>();
                foreach (var property in _filterProperties)
                {
                    var filterValue = property.Item2.GetValue(searchOptions);
                    if (filterValue != null)
                    {
                        // DateTimes need to be formatted to conform to ISO
                        if (filterValue is DateTime dateTime)
                            filterValue = dateTime.ToString("O");
                        queryProps.Add($"{property.Item1}={filterValue}");
                    }
                }

                // Aggregating an empty list == exception 
                if (queryProps.Count != 0)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append('?').Append(queryProps.Aggregate((a, b) => $"{a}&{b}"));
                    searchURL += sb.ToString();
                }
            }
            Console.WriteLine(searchURL);

            var result = await GetBeatmapsFromPage(searchURL, token);
            if (result is null)
                return null;

            return new SearchPage(page, searchOptions, result)
            {
                Client = this
            };
        }

        #endregion

        #region Users

        public async Task<User?> User(int id, CancellationToken? token = null)
        {
            if (_fetchedUsers.TryGetValue(id, out User? user))
            {
                // TODO: Update user... probably.
                return user;
            }

            var response = await _httpService.GetAsync("users/id/" + id, token);
            if (!response.Successful)
                return null;

            user = await response.ReadAsObjectAsync<User>();
            GetOrAddUserToCache(user, out user);
            return user;
        }

        public async Task<User?> User(string name, CancellationToken? token = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            name = name.ToLowerInvariant();
            if (_fetchedUsernames.TryGetValue(name, out User? user))
            {
                // TODO: Update user... probably.
                return user;
            }

            var response = await _httpService.GetAsync("search/text/0?q=" + name, token).ConfigureAwait(false);
            if (!response.Successful)
                return null;

            var page = await response.ReadAsObjectAsync<SerializableSearch>();
            if (page.Docs.Count == 0)
                return null;

            if (page.User is null)
                return null;

            GetOrAddUserToCache(page.User, out user);
            return user;
        }

        #endregion

        private async Task<IReadOnlyList<Beatmap>?> GetBeatmapsFromPage(string url, CancellationToken? token = null)
        {
            var response = await _httpService.GetAsync(url, token).ConfigureAwait(false);
            if (!response.Successful)
                return null;

            var page = await response.ReadAsObjectAsync<SerializableSearch>();
            if (page.Docs.Count == 0)
                return null;

            // We do this so there's only one source of a Beatmap object that exists
            // This way, Beatmaps will constantly have their properties updated as soon
            // as something else sees a newer version of it.
            List<Beatmap> beatmapList = new List<Beatmap>();
            foreach (var beatmap in page.Docs)
            {
                GetOrAddBeatmapToCache(beatmap, out Beatmap selfOrCached);
                beatmapList.Add(selfOrCached);
            }

            return new ReadOnlyCollection<Beatmap>(beatmapList);
        }

        /// <summary>
        /// Gets or adds a map to the cache. This will give the beatmap properties their client object if necessary.
        /// </summary>
        /// <param name="beatmap">The beatmap to get or add.</param>
        /// <param name="cachedAndOrBeatmap">The added or fetched Beatmap</param>
        /// <returns>Returns true if it was added. Returns false if it was already cached.</returns>
        private bool GetOrAddBeatmapToCache(Beatmap beatmap, out Beatmap cachedAndOrBeatmap)
        {
            if (_fetchedBeatmaps.TryGetValue(beatmap.ID, out Beatmap? cachedBeatmap))
            {
                // TODO: Refresh beatmap, probably.
                cachedAndOrBeatmap = cachedBeatmap;
                return false;
            }
            else
            {
                _fetchedBeatmaps.TryAdd(beatmap.ID, beatmap);
                cachedAndOrBeatmap = beatmap;

                foreach (var version in beatmap.Versions)
                {
                    _fetchedHashedBeatmaps.TryAdd(version.Hash, beatmap);
                }

                PopulateWithClient(cachedAndOrBeatmap);
                return true;
            }
        }

        private bool GetOrAddUserToCache(User user, out User cachedAndOrUser)
        {
            if (_fetchedUsers.TryGetValue(user.ID, out User? cachedUser))
            {
                // Update the stats field on any updated user objects.
                if (user.Stats != null && cachedUser.Stats is null)
                {
                    cachedUser.Stats = user.Stats;
                }
                else if (cachedUser.Stats != null)
                {
                    if (!cachedUser.Stats.Equals(user.Stats))
                        cachedUser.Stats = user.Stats;
                }
                cachedAndOrUser = cachedUser;
                return false;
            }
            else
            {
                _fetchedUsers.TryAdd(user.ID, user);
                cachedAndOrUser = user;

                _fetchedUsernames.TryAdd(user.Name.ToLower(), user);
                if (!user.HasClient)
                    user.Client = this;
                return true;
            }
        }

        internal void PopulateWithClient(Beatmap beatmap)
        {
            if (!beatmap.HasClient)
                beatmap.Client = this;

            if (!beatmap.Uploader.HasClient)
            {
                GetOrAddUserToCache(beatmap.Uploader, out var uploader);
                beatmap.Uploader = uploader;
            }

            foreach (var version in beatmap.Versions)
            {
                if (!version.HasClient)
                {
                    version.Client = this;
                    if (version.Testplays != null)
                    {
                        foreach (var testplay in version.Testplays)
                        {
                            if (!testplay.HasClient)
                                testplay.Client = this;
                            if (!testplay.User.HasClient)
                            {
                                GetOrAddUserToCache(testplay.User, out var testplayer);
                                testplay.User = testplayer;
                            }
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            if (_httpService is IDisposable disposable)
                disposable.Dispose();
            IsDisposed = true;
        }
    }
}