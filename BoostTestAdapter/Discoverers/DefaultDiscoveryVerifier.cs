// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.IO;
using System.Security.Policy;

namespace BoostTestAdapter.Discoverers
{
    class DefaultDiscoveryVerifier : IDiscoveryVerifier
    {
        public bool FileExists(string path)
        {
            return File.Exists(path);
        }

        public bool IsFileZoneMyComputer(string path)
        {
            var zone = Zone.CreateFromUrl(path);
            return zone.SecurityZone == System.Security.SecurityZone.MyComputer || zone.SecurityZone == System.Security.SecurityZone.Intranet;
        }
    }
}
