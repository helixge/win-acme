﻿using PKISharp.WACS.Clients;
using PKISharp.WACS.Plugins.Base;
using PKISharp.WACS.Plugins.Interfaces;
using PKISharp.WACS.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using PKISharp.WACS.Extensions;

namespace PKISharp.WACS.Plugins.TargetPlugins
{
    internal class IISSitesFactory : BaseTargetPluginFactory<IISSites>
    {
        public const string SiteServer = "IISSiteServer";
        public override bool Hidden => _iisClient.Version.Major == 0;
        protected IISClient _iisClient;

        public IISSitesFactory(ILogService log, IISClient iisClient) : 
            base(log, nameof(IISSites), "SAN certificate for all bindings of multiple IIS sites")
        {
            _iisClient = iisClient;
        }
    }

    internal class IISSites : IISSite, ITargetPlugin
    {
        public IISSites(ILogService log, IISClient iisClient) : base(log, iisClient) {}

        Target ITargetPlugin.Default(IOptionsService optionsService) {
            var rawSiteId = optionsService.TryGetRequiredOption(nameof(optionsService.Options.SiteId), optionsService.Options.SiteId);
            var totalTarget = GetCombinedTarget(GetSites(false, false), rawSiteId);
            totalTarget.ExcludeBindings = optionsService.Options.ExcludeBindings;
            totalTarget.CommonName = optionsService.Options.CommonName;
            if (!totalTarget.IsCommonNameValid(_log)) return null;
            return totalTarget;
        }

        private Target GetCombinedTarget(List<Target> targets, string sanInput)
        {
            var siteList = new List<Target>();
            if (string.Equals(sanInput,"s", StringComparison.InvariantCultureIgnoreCase))
            {
                siteList.AddRange(targets);
            }
            else
            {
                var siteIDs = sanInput.Trim().Trim(',').Split(',').Distinct().ToArray();
                foreach (var idString in siteIDs)
                {
                    var id = -1;
                    if (int.TryParse(idString, out id))
                    {
                        var site = targets.Where(t => t.TargetSiteId == id).FirstOrDefault();
                        if (site != null)
                        {
                            siteList.Add(site);
                        }
                        else
                        {
                            _log.Warning($"SiteId '{idString}' not found");
                        }
                    }
                    else
                    {
                        _log.Warning($"Invalid SiteId '{idString}', should be a number");
                    }
                }
                if (siteList.Count == 0)
                {
                    _log.Warning($"No valid sites selected");
                    return null;
                }
            }
            var totalTarget = new Target
            {
                Host = string.Join(",", siteList.Select(x => x.TargetSiteId)),
                HostIsDns = false,
                IIS = true,
                TargetSiteId = -1,
                ValidationSiteId = null, 
                InstallationSiteId = null,
                FtpSiteId = null,
                AlternativeNames = siteList.SelectMany(x => x.AlternativeNames).Distinct().ToList()
            };
            return totalTarget;
        }

        Target ITargetPlugin.Aquire(IOptionsService optionsService, IInputService inputService, RunLevel runLevel)
        {
            var targets = GetSites(optionsService.Options.HideHttps, true).Where(x => x.Hidden == false).ToList();
            inputService.WritePagedList(targets.Select(x => Choice.Create(x, $"{x.Host} ({x.AlternativeNames.Count()} bindings) [@{x.WebRootPath}]", x.TargetSiteId.ToString())).ToList());
            var sanInput = inputService.RequestString("Enter a comma separated list of site IDs, or 'S' to run for all sites").ToLower().Trim();
            var totalTarget = GetCombinedTarget(targets, sanInput);
            inputService.WritePagedList(totalTarget.AlternativeNames.Select(x => Choice.Create(x, "")));
            totalTarget.ExcludeBindings = inputService.RequestString("Press enter to include all listed hosts, or type a comma-separated lists of exclusions");
            if (runLevel >= RunLevel.Advanced) totalTarget.AskForCommonNameChoice(inputService);
            return totalTarget;
        }

        Target ITargetPlugin.Refresh(Target scheduled)
        {
            // TODO: check if the sites still exist, log removed sites
            // and return null if none of the sites can be found (cancel
            // the renewal of the certificate). Maybe even save the "S"
            // switch somehow to add sites if new ones are added to the 
            // server.
            return scheduled;
        }

        public override IEnumerable<Target> Split(Target scheduled)
        {
            var targets = GetSites(false, false);
            var siteIDs = scheduled.Host.Split(',');
            var filtered = targets.Where(t => siteIDs.Contains(t.TargetSiteId.ToString())).ToList();
            filtered.ForEach(x => {
                x.SSLPort = scheduled.SSLPort;
                x.ValidationPort = scheduled.ValidationPort;
                x.ValidationSiteId = scheduled.ValidationSiteId;
                x.InstallationSiteId = scheduled.InstallationSiteId;
                x.FtpSiteId = scheduled.FtpSiteId;
                x.ExcludeBindings = scheduled.ExcludeBindings;
                x.ValidationPluginName = scheduled.ValidationPluginName;
                x.DnsAzureOptions = scheduled.DnsAzureOptions;
                x.DnsScriptOptions = scheduled.DnsScriptOptions;
                x.HttpFtpOptions = scheduled.HttpFtpOptions;
                x.HttpWebDavOptions = scheduled.HttpWebDavOptions;
            });
            return filtered.Where(x => x.GetHosts(true, true).Count > 0).ToList();
        }
    }
}