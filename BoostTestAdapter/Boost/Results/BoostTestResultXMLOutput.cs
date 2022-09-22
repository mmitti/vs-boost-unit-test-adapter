﻿// (C) Copyright ETAS 2015.
// Distributed under the Boost Software License, Version 1.0.
// (See accompanying file LICENSE_1_0.txt or copy at
// http://www.boost.org/LICENSE_1_0.txt)

using System;
using System.Globalization;
using System.Collections.Generic;

namespace BoostTestAdapter.Boost.Results
{

    /// <summary>
    /// Boost Test Result abstraction needed for the proper handling of XML documents.
    /// </summary>
    public abstract class BoostTestResultXMLOutput : BoostTestResultOutputBase
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="target">The destination result collection. Possibly used for result aggregation.</param>
        protected BoostTestResultXMLOutput(IDictionary<string, TestResult> target)
            : base(target)
        {
        }

        #region BoostTestResultOutputBase

        public override IDictionary<string, TestResult> Parse(string content)
        {
            content = ParseCDataSection(content);
            content = RemoveNullTerminators(content);
            content = AddXmlDeclaration(content);

            return ParseXml(content);
        }

        #endregion

        /// <summary>
        /// Parses the XML report from the provided XML string
        /// </summary>
        /// <param name="xml">The XML string content</param>
        /// <returns>The test result collection containing the parsed information</returns>
        protected abstract IDictionary<string, TestResult> ParseXml(string xml);

        /// <summary>
        /// Boost UTF does not add any XML declaration in the file so add it
        /// such that more formal XML parsers don't complain.
        /// An encoding declaration is pointless as the content has
        /// already been decoded and converted into UTF-16 by virtue of being in a string.
        /// </summary>
        /// <param name="content">The xml file contents</param>
        /// <returns>The modified xml file contents with a declaration</returns>
        private static string AddXmlDeclaration(string content)
        {
            if (!content.StartsWith("<?xml", StringComparison.Ordinal))
            { 
                content = content.Insert(0, "<?xml version=\"1.0\"?>\n");
            }

            return content;
        }

        /// <summary>
        /// Removes all null terminators which can be possibly located in the input value
        /// </summary>
        /// <param name="value">The string to be filtered.</param>
        /// <returns>The filtered string.</returns>
        private static string RemoveNullTerminators(string value)
        {
            return value.Replace('\0', ' ');
        }

        /// <summary>
        /// Replaces all the control characters (less than 32) in the CDATA section with the hexadecimal representation.
        /// </summary>
        /// <param name="fileContent">The XML content to be filtered.</param>
        /// <returns>The filtered content.</returns>
        /// <remarks>
        /// Until version 1.58 Boost Unit Test Library outputs non-filtered content of arrays in the log/report.
        /// This function fixes the output to avoid XML parsing crashes using the same approach the library uses starting from version 1.59.
        /// </remarks>
        private static string ParseCDataSection(string fileContent)
        {
            var startPos = fileContent.IndexOf("<![CDATA[", StringComparison.Ordinal);
            while (startPos > -1)
            {
                var endPos = fileContent.IndexOf("]]>", startPos, StringComparison.Ordinal);

                var dataSectionContent = fileContent.Substring(startPos, endPos - startPos);

                for (int i = 0; i < 32; i++)
                {
                    if (i == 10 || i == 13)
                        continue;

                    string c = char.ConvertFromUtf32(i);
                    dataSectionContent = dataSectionContent.Replace(c, string.Format(CultureInfo.InvariantCulture, "0x{0:X2}", i));
                }

                fileContent = fileContent.Substring(0, startPos) + dataSectionContent + fileContent.Substring(endPos);

                startPos = fileContent.IndexOf("<![CDATA[", endPos, StringComparison.Ordinal);
            }

            return fileContent;
        }
    }
}
