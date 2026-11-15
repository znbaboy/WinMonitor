using WinMonitor.Services;

namespace WinMonitor.Core
{
    public static class MetricLabelResolver
    {
        /// <summary>
        /// Resolves an item's name.
        /// Order: UserLabel > live label (InfoService) > DynamicLabel (settings)
        /// </summary>
        public static string ResolveLabel(MonitorItemConfig config)
        {
            if (config == null) return "";

            // 1. The user's name
            if (!string.IsNullOrEmpty(config.UserLabel)) 
                return config.UserLabel;
            
            // 2. Live label injected at runtime
            // (stored in InfoService as PROP.Label.<key>)
            // Use cached key if available
            string dynKey = !string.IsNullOrEmpty(config.CachedPropLabelKey) 
                ? config.CachedPropLabelKey 
                : "PROP.Label." + config.Key;

            string dynVal = InfoService.Instance.GetValue(dynKey);
            
            // Skip empty or ERROR values (avoids flicker)
            if (!string.IsNullOrEmpty(dynVal)) 
                return dynVal;

            // 3. Last known dynamic label saved in the settings
            if (!string.IsNullOrEmpty(config.DynamicLabel)) 
                return config.DynamicLabel;

            // 4. Nothing: caller uses the default
            return ""; 
        }

        /// <summary>
        /// Resolves an item's short label.
        /// Order: TaskbarLabel > live label (InfoService) > DynamicTaskbarLabel (settings)
        /// </summary>
        public static string ResolveShortLabel(MonitorItemConfig config)
        {
            if (config == null) return "";

            // 1. The user's short label
            if (!string.IsNullOrEmpty(config.TaskbarLabel)) 
                return config.TaskbarLabel;

            // 2. Live label injected at runtime
            string dynKey = !string.IsNullOrEmpty(config.CachedPropShortLabelKey)
                ? config.CachedPropShortLabelKey
                : "PROP.ShortLabel." + config.Key;
            
            string dynVal = InfoService.Instance.GetValue(dynKey);
            if (!string.IsNullOrEmpty(dynVal)) 
                return dynVal;

            return "";
        }
    }
}