// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using BoostTestShared;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Management;

namespace BoostTestAdapter.Utility.VisualStudio
{
    /// <summary>
    /// Default implementation of an IBoostTestPackageServiceFactory. Provides IBoostTestPackageServiceWrapper instance
    /// for a proxy of the service running in the parent Visual Studio process.
    /// </summary>
    class DefaultBoostTestPackageServiceFactory : IBoostTestPackageServiceFactory
    {
        #region IBoostTestPackageServiceFactory

        public IBoostTestPackageServiceWrapper Create()
        {
            int processId = Process.GetCurrentProcess().Id;
            int parentProcessId = GetParentVSProcessId(processId);
            var proxy = BoostTestPackageServiceConfiguration.CreateProxy(parentProcessId);
            return new BoostTestPackageServiceProxyWrapper(proxy);
        }

        #endregion

        /// <summary>
        /// Gets the process id of the parent Visual Studio process.
        /// </summary>
        /// <param name="processId">The process id of the child process.</param>
        /// <returns></returns>
        private static int GetParentVSProcessId(int processId)
        {
            string processIdString = processId.ToString(CultureInfo.InvariantCulture);
            string query = "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = " + processIdString;
            uint parentId;

            using (ManagementObjectSearcher search = new ManagementObjectSearcher("root\\CIMV2", query))
            {
                ManagementObjectCollection.ManagementObjectEnumerator results = search.Get().GetEnumerator();
                results.MoveNext();
                ManagementBaseObject queryObj = results.Current;
                parentId = (uint)queryObj["ParentProcessId"];
                Process parent = Process.GetProcessById((int)parentId);
                if (parent.ProcessName == "explorer")
                {
                    // We've gone too far, we're probably being invoked from the command line.
                    // There's nothing we can do at this point, throw an error to be caught and debug-logged.
                    throw new Exception(Resources.VSProcessNotFound);
                }

                if (parent.ProcessName != "devenv")
                    return GetParentVSProcessId((int)parentId);
            }

            return (int)parentId;
        }
    }
}
