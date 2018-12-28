﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace DirectSp.Core
{
    internal class UserSessionManager
    {
        public int SessionTimeout { get; }
        public UserSessionManager(int sessionTimeout)
        {
            SessionTimeout = sessionTimeout;
        }

        private ConcurrentDictionary<string, UserSession> UserSessions = new ConcurrentDictionary<string, UserSession>();
        public UserSession GetUserSession(string appName, string userId, string audience)
        {
            //try cleanup on each request
            CleanUp();

            //Create or get userSession
            var sessionKey = userId + "#" + audience;
            if (!UserSessions.TryGetValue(sessionKey, out UserSession userSession))
            {
                userSession = new UserSession(new InvokeContext(appName, userId, audience));
                UserSessions[sessionKey] = userSession;
            }

            return userSession;
        }

        private object CleaningLock = new object();
        private DateTime LastCleanupTime = DateTime.Now;

        private void CleanUp()
        {
            if (!Monitor.TryEnter(CleaningLock))
                return;

            try
            {
                //Check the cleanup time
                if (LastCleanupTime.AddSeconds(SessionTimeout) > DateTime.Now)
                    return;

                //find expired sessions
                var expired = new List<string>();
                foreach (var item in UserSessions)
                {
                    if (item.Value.LastWriteTime.AddSeconds(SessionTimeout) < DateTime.Now)
                        expired.Add(item.Key);
                }

                //clear expired sessions
                foreach (var item in expired)
                {
                    UserSessions.TryRemove(item, out UserSession userSession);
                }

                LastCleanupTime = DateTime.Now;
            }
            finally
            {
                Monitor.Exit(CleaningLock);
            }
        }

    }
}
