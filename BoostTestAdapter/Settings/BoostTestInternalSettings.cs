// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.VisualStudio.TestWindow.Extensibility;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using System.Xml.XPath;

namespace BoostTestAdapter.Settings
{
    public static class BoostTestSettingsConstants
    {
        public const string InternalSettingsName = "BoostTestInternalSettings";
    }

    [Export(typeof(IRunSettingsService))]
    [SettingsName(BoostTestSettingsConstants.InternalSettingsName)]
    public class RunSettingsService : IRunSettingsService
    {
        public string Name => BoostTestSettingsConstants.InternalSettingsName;

        public RunSettingsService()
        {
        }

        public IXPathNavigable AddRunSettings(IXPathNavigable runSettingDocument,
            IRunSettingsConfigurationInfo configurationInfo, ILogger logger)
        {
            XPathNavigator runSettingsNavigator = runSettingDocument.CreateNavigator();
            Debug.Assert(runSettingsNavigator != null, "userRunSettingsNavigator == null!");
            if (!runSettingsNavigator.MoveToChild(Constants.RunSettingsName, ""))
            {
                return runSettingsNavigator;
            }

            var settingsContainer = new RunSettingsContainer();

            runSettingsNavigator.MoveToChild(Constants.RunSettingsName, "");
            runSettingsNavigator.AppendChild(settingsContainer.ToXml().CreateNavigator());

            runSettingsNavigator.MoveToRoot();
            return runSettingsNavigator;
        }
    }

    [XmlRoot(BoostTestSettingsConstants.InternalSettingsName)]
    public class RunSettingsContainer : TestRunSettings
    {
        public RunSettingsContainer()
            : base(BoostTestSettingsConstants.InternalSettingsName)
        {
            VSProcessId = Process.GetCurrentProcess().Id;
        }

        public int VSProcessId { get; set; }

        public override XmlElement ToXml()
        {
            var document = new XmlDocument();
            using (XmlWriter writer = document.CreateNavigator().AppendChild())
            {
                new XmlSerializer(typeof(RunSettingsContainer))
                    .Serialize(writer, this);
            }
            return document.DocumentElement;
        }
    }

    [Export(typeof(ISettingsProvider))]
    [SettingsName(BoostTestSettingsConstants.InternalSettingsName)]
    public class RunSettingsProvider : ISettingsProvider
    {
        public string Name => BoostTestSettingsConstants.InternalSettingsName;

        public int VSProcessId { get; set; }

        public void Load(XmlReader reader)
        {
            if (reader.Read() && reader.Name.Equals(this.Name))
            {
                var serializer = new XmlSerializer(typeof(RunSettingsContainer));
                RunSettingsContainer settings = serializer.Deserialize(reader) as RunSettingsContainer;
                this.VSProcessId = settings.VSProcessId;
            }
        }
    }
}
