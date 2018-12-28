﻿using System;
using System.Linq;
using System.Security.Claims;

namespace DirectSp.Core
{
    class UserSession
    {
        public UserSession(InvokeContext spContext)
        {
            SpContext = spContext;
        }

        private readonly object LockObject = new object();
        private DateTime _LastRequestTime;
        public DateTime LastWriteTime
        {
            get
            {
                lock (LockObject)
                    return _LastRequestTime;
            }
        }
        public void SetWriteMode(bool isWriteMode)
        {
            lock (LockObject)
            {
                _RequestCount++;
                if (isWriteMode)
                    _LastRequestTime = DateTime.Now;
            }
        }


        private DateTime _RequestIntervalStartTime;
        public DateTime RequestIntervalStartTime
        {
            get
            {
                lock (LockObject)
                    return _RequestIntervalStartTime;
            }
        }

        private int _RequestCount;
        public int RequestCount
        {
            get
            {
                lock (LockObject)
                    return _RequestCount;
            }
        }

        public void ResetRequestCount()
        {
            lock (LockObject)
            {
                _RequestIntervalStartTime = DateTime.Now;
                _RequestCount = 0;
            }
        }

        private InvokeContext _SpContext;
        public InvokeContext SpContext
        {
            get
            {
                lock (LockObject)
                    return _SpContext;
            }
            set
            {
                lock (LockObject)
                    _SpContext = value;
            }
        }

    }
}
