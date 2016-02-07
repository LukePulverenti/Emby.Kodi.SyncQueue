﻿using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Emby.Kodi.SyncQueue.Helpers;
using System.Threading.Tasks;
using MediaBrowser.Model.Session;

namespace Emby.Kodi.SyncQueue.EntryPoints
{
    class UserSyncNotification : IServerEntryPoint
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILogger _logger;
        private readonly IUserDataManager _userDataManager;
        private readonly IUserManager _userManager;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IApplicationPaths _applicationPaths;

        private readonly object _syncLock = new object();
        private Timer UpdateTimer { get; set; }
        private const int UpdateDuration = 500;

        private readonly Dictionary<Guid, List<IHasUserData>> _changedItems = new Dictionary<Guid, List<IHasUserData>>();

        private DataHelper dataHelper = null;
        private CancellationTokenSource cTokenSource = new CancellationTokenSource();
        private CancellationToken cToken;

        public UserSyncNotification(IUserDataManager userDataManager, ISessionManager sessionManager, ILogger logger, IUserManager userManager, IJsonSerializer jsonSerializer, IApplicationPaths applicationPaths)
        {
            _userDataManager = userDataManager;
            _sessionManager = sessionManager;
            _logger = logger;
            _userManager = userManager;
            _jsonSerializer = jsonSerializer;
            _applicationPaths = applicationPaths;
            //dataHelper = new DataHelper(_logger, _jsonSerializer);
        }

        public void Run()
        {
            _userDataManager.UserDataSaved += _userDataManager_UserDataSaved;

            _logger.Info("Emby.Kodi.SyncQueue:  UserSyncNotification Startup...");
            dataHelper = new DataHelper(_logger, _jsonSerializer);
            string dataPath = _applicationPaths.DataPath;
            bool result;

            result = dataHelper.CheckCreateFiles(dataPath);

            if (result)
                result = dataHelper.OpenConnection();
            if (result)
                result = dataHelper.CreateUserTable("UserInfoChangedQueue", "UICQUnique");
            
            if (!result)
            {
                throw new ApplicationException("Emby.Kodi.SyncQueue:  Could Not Be Loaded Due To Previous Error!");
            }
            cToken = cTokenSource.Token;
        }

        void _userDataManager_UserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            if (e.SaveReason == UserDataSaveReason.PlaybackProgress)
            {
                return;
            }

            lock (_syncLock)
            {
                if (UpdateTimer == null)
                {
                    UpdateTimer = new Timer(UpdateTimerCallback, null, UpdateDuration,
                                                   Timeout.Infinite);
                }
                else
                {
                    UpdateTimer.Change(UpdateDuration, Timeout.Infinite);
                }

                List<IHasUserData> keys;

                if (!_changedItems.TryGetValue(e.UserId, out keys))
                {
                    keys = new List<IHasUserData>();
                    _changedItems[e.UserId] = keys;
                }

                keys.Add(e.Item);

                var baseItem = e.Item as BaseItem;

                // Go up one level for indicators
                if (baseItem != null)
                {
                    var parent = baseItem.Parent;

                    if (parent != null)
                    {
                        keys.Add(parent);
                    }
                }
            }
        }

        private void UpdateTimerCallback(object state)
        {
            lock (_syncLock)
            {
                // Remove dupes in case some were saved multiple times
                var changes = _changedItems.ToList();
                _changedItems.Clear();

                Task x = SendNotifications(changes, cToken);

                if (UpdateTimer != null)
                {
                    UpdateTimer.Dispose();
                    UpdateTimer = null;
                }
            }
        }

        private async Task SendNotifications(IEnumerable<KeyValuePair<Guid, List<IHasUserData>>> changes, CancellationToken cancellationToken)
        {
            foreach (var pair in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var userId = pair.Key;
                _logger.Debug(String.Format("Emby.Kodi.SyncQueue:  Starting to save items for {0}", userId.ToString()));

                var user = _userManager.GetUserById(userId);

                var dtoList = pair.Value
                       .GroupBy(i => i.Id)
                       .Select(i => i.First())
                       .Select(i =>
                       {
                           var dto = _userDataManager.GetUserDataDto(i, user);
                           dto.ItemId = i.Id.ToString("N");
                           return dto;
                       })
                       .ToList();

                _logger.Debug(String.Format("Emby.Kodi.SyncQueue:  SendNotification:  User = '{0}' dtoList = '{1}'", userId.ToString("N"), _jsonSerializer.SerializeToString(dtoList).ToString()));

                Task<int> saveUser = SaveUserChanges(dtoList, userId.ToString("N"), "UserInfoChangedQueue", cancellationToken);

                int iSaveUser = await saveUser;
            }
        }

        private async Task<int> SaveUserChanges(List<MediaBrowser.Model.Dto.UserItemDataDto> dtos, string user, string tableName, CancellationToken cancellationToken)
        {
            try
            {
                IEnumerable<Task<int>> LibraryAddItemQuery =
                    from dto in dtos select dataHelper.UserChangeSetItem(dto, user, dto.ItemId, tableName, cancellationToken);

                Task<int>[] addTasks = LibraryAddItemQuery.ToArray();

                int[] itemCount = await Task.WhenAll(addTasks);
                return 1;
            }
            catch (Exception e)
            {
                _logger.Error("Emby.Kodi.SyncQueue:  Emby.Kodi.SyncQueue:  Error in AlterLibrary...");
                _logger.ErrorException(e.Message, e);
                return 0;
            }
        }

        private void TriggerCancellation()
        {            
            cTokenSource.Cancel();
        }

        public void Dispose()
        {
            if (!cToken.IsCancellationRequested)
            {
                TriggerCancellation();
            }
            Dispose(true);
        }

        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                if (UpdateTimer != null)
                {
                    UpdateTimer.Dispose();
                    UpdateTimer = null;
                }
                if (dataHelper != null)
                {
                    dataHelper.Dispose();
                    dataHelper = null;
                }

                _userDataManager.UserDataSaved -= _userDataManager_UserDataSaved;
            }
        }
    }
}
