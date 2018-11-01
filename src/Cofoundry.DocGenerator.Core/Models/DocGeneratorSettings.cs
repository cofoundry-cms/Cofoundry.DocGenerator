using System;
using System.Collections.Generic;
using System.Text;

namespace Cofoundry.DocGenerator
{
    public class DocGeneratorSettings
    {
        public bool UseAzure { get; set; }

        public string Version { get; set; }

        public string SourcePath { get; set; }

        public string OutputPath { get; set; }

        public bool CleanDestination { get; set; }

        public string BlobStorageConnectionString { get; set; }
    }
}
