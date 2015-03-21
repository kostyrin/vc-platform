﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Caching;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web;
using System.Web.Configuration;
using System.Web.Mvc;
using System.Web.UI;
using VirtoCommerce.Web.Client.Helpers;

namespace VirtoCommerce.Web.Client.Extensions.Filters.Caching
{
	[SuppressMessage("Microsoft.Performance", "CA1813:AvoidUnsealedAttributes", Justification = "Unsealed so that subclassed types can set properties in the default constructor.")]
	[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
	public class CustomOutputCacheAttribute : ActionFilterAttribute, IExceptionFilter
	{

		private readonly OutputCacheParameters _cacheSettings;
        private OutputCacheProfile _cacheConfig = null;
		private const string CacheKeyPrefix = "_MvcChildActionCache_";
		private static ObjectCache _childActionCache;
		private readonly Func<ObjectCache> _childActionCacheThunk = () => ChildActionCache;
		private static readonly object ChildActionFilterFinishCallbackKey = new object();
		private bool _locationWasSet;
		private bool _noStoreWasSet;

		public CustomOutputCacheAttribute()
		{
            _cacheSettings = new OutputCacheParameters { VaryByParam = "*"};
		}

		internal CustomOutputCacheAttribute(ObjectCache childActionCache)
		{
			_childActionCacheThunk = () => childActionCache;
		}

        internal OutputCacheParameters CacheSettings
        {
            get
            {
                return _cacheSettings;
            }
        }

        internal OutputCacheProfile CacheConfig
        {
            get
            {
                if (_cacheConfig == null)
                {
                    var section = (OutputCacheSettingsSection)ConfigurationManager.GetSection("system.web/caching/outputCacheSettings");
                    if ((section != null) && (section.OutputCacheProfiles.Count > 0))
                    {
                        _cacheConfig = section.OutputCacheProfiles[CacheProfile];
                    }
                }
                return _cacheConfig;
            }
        }

		public string CacheProfile
		{
			get
			{
				return _cacheSettings.CacheProfile ?? String.Empty;
			}
			set
			{
				_cacheSettings.CacheProfile = value;
			}
		}

		public bool Enabled
		{
			get
			{
				return _cacheSettings.Enabled;
			}
			set
			{
				_cacheSettings.Enabled = value;
			}
		}

		public static ObjectCache ChildActionCache
		{
			get
			{
				return _childActionCache ?? MemoryCache.Default;
			}
			set
			{
				_childActionCache = value;
			}
		}

		private ObjectCache ChildActionCacheInternal
		{
			get
			{
				return _childActionCacheThunk();
			}
		}

		public int Duration
		{
			get
			{
				return _cacheSettings.Duration;
			}
			set
			{
				_cacheSettings.Duration = value;
			}
		}

		public OutputCacheLocation Location
		{
			get
			{
				return _cacheSettings.Location;
			}
			set
			{
				_cacheSettings.Location = value;
				_locationWasSet = true;
			}
		}

		public bool NoStore
		{
			get
			{
				return _cacheSettings.NoStore;
			}
			set
			{
				_cacheSettings.NoStore = value;
				_noStoreWasSet = true;
			}
		}

		public string SqlDependency
		{
			get
			{
				return _cacheSettings.SqlDependency ?? String.Empty;
			}
			set
			{
				_cacheSettings.SqlDependency = value;
			}
		}

		public string VaryByContentEncoding
		{
			get
			{
				return _cacheSettings.VaryByContentEncoding ?? String.Empty;
			}
			set
			{
				_cacheSettings.VaryByContentEncoding = value;
			}
		}

		public string VaryByCustom
		{
			get
			{
				return _cacheSettings.VaryByCustom ?? String.Empty;
			}
			set
			{
				_cacheSettings.VaryByCustom = value;
			}
		}

		public string VaryByHeader
		{
			get
			{
				return _cacheSettings.VaryByHeader ?? String.Empty;
			}
			set
			{
				_cacheSettings.VaryByHeader = value;
			}
		}

		[SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "Param", Justification = "Matches the @ OutputCache page directive.")]
		public string VaryByParam
		{
			get
			{
				return _cacheSettings.VaryByParam ?? String.Empty;
			}
			set
			{
				_cacheSettings.VaryByParam = value;
			}
		}

		private static void ClearChildActionFilterFinishCallback(ControllerContext controllerContext)
		{
			controllerContext.HttpContext.Items.Remove(ChildActionFilterFinishCallbackKey);
		}

		private static void CompleteChildAction(ControllerContext filterContext, bool wasException)
		{
			Action<bool> callback = GetChildActionFilterFinishCallback(filterContext);

			if (callback != null)
			{
				ClearChildActionFilterFinishCallback(filterContext);
				callback(wasException);
			}
		}

		private static Action<bool> GetChildActionFilterFinishCallback(ControllerContext controllerContext)
		{
			return controllerContext.HttpContext.Items[ChildActionFilterFinishCallbackKey] as Action<bool>;
		}

		internal string GetChildActionUniqueId(ActionExecutingContext filterContext)
		{
			var uniqueIdBuilder = new StringBuilder();

			// Start with a prefix, presuming that we share the cache with other users
			uniqueIdBuilder.Append(CacheKeyPrefix);

			// Unique ID of the action description
			uniqueIdBuilder.Append(filterContext.ActionDescriptor.UniqueId);

			// Unique ID from the VaryByCustom settings, if any
			uniqueIdBuilder.Append(MyDescriptorUtil.CreateUniqueId(VaryByCustom));
			if (!String.IsNullOrEmpty(VaryByCustom))
			{
				string varyByCustomResult = filterContext.HttpContext.ApplicationInstance.GetVaryByCustomString(HttpContext.Current, VaryByCustom);
				uniqueIdBuilder.Append(varyByCustomResult);
			}

			// Unique ID from the VaryByParam settings, if any
			uniqueIdBuilder.Append(GetUniqueIdFromActionParameters(filterContext, SplitVaryByParam(VaryByParam)));

			// The key is typically too long to be useful, so we use a cryptographic hash
			// as the actual key (better randomization and key distribution, so small vary
			// values will generate dramatically different keys).
			using (SHA256 sha = SHA256.Create())
			{
				return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(uniqueIdBuilder.ToString())));
			}
		}

		private static string GetUniqueIdFromActionParameters(ActionExecutingContext filterContext, IEnumerable<string> keys)
		{
			// Generate a unique ID of normalized key names + key values
			var keyValues = new Dictionary<string, object>(filterContext.ActionParameters, StringComparer.OrdinalIgnoreCase);
			keys = (keys ?? keyValues.Keys).Select(key => key.ToUpperInvariant())
										   .OrderBy(key => key, StringComparer.Ordinal);

			return MyDescriptorUtil.CreateUniqueId(keys.Concat(keys.Select(key => keyValues.ContainsKey(key) ? keyValues[key] : null)));
		}

		public static bool IsChildActionCacheActive(ControllerContext controllerContext)
		{
			return GetChildActionFilterFinishCallback(controllerContext) != null;
		}

		public override void OnActionExecuted(ActionExecutedContext filterContext)
		{
            //Immediatly return if cache is disabled from admin
            if (!SettingsHelper.OutputCacheEnabled)
                return;

			if (filterContext == null)
			{
				throw new ArgumentNullException("filterContext");
			}

			// Complete the request if the child action threw an exception
            if (filterContext.IsChildAction && filterContext.Exception != null && SettingsHelper.ChildOutputCacheEnabled)
			{
				CompleteChildAction(filterContext, true);
			}
		}

		public override void OnActionExecuting(ActionExecutingContext filterContext)
		{
            //Immediatly return if cache is disabled from admin
            if (!SettingsHelper.OutputCacheEnabled)
                return;

			if (filterContext == null)
			{
				throw new ArgumentNullException("filterContext");
			}
			var configSection = (OutputCacheSection)ConfigurationManager.GetSection("system.web/caching/outputCache");

			if (configSection.EnableOutputCache)
			{

				if (filterContext.IsChildAction && SettingsHelper.ChildOutputCacheEnabled)
				{
					ValidateChildActionConfiguration();

					// Already actively being captured? (i.e., cached child action inside of cached child action)
					// Realistically, this needs write substitution to do properly (including things like authentication)
					if (GetChildActionFilterFinishCallback(filterContext) != null)
					{
						throw new InvalidOperationException("Cannot nest child cache");
					}

					// Already cached?
					var uniqueId = GetChildActionUniqueId(filterContext);
					var cachedValue = ChildActionCacheInternal.Get(uniqueId) as string;
					if (cachedValue != null)
					{
						filterContext.Result = new ContentResult { Content = cachedValue };
						return;
					}

					// Swap in a new TextWriter so we can capture the output
					var cachingWriter = new StringWriter(CultureInfo.InvariantCulture);
					var originalWriter = filterContext.HttpContext.Response.Output;
					filterContext.HttpContext.Response.Output = cachingWriter;

					// Set a finish callback to clean up
					SetChildActionFilterFinishCallback(filterContext, wasException =>
					{
						// Restore original writer
						filterContext.HttpContext.Response.Output = originalWriter;

						// Grab output and write it
						string capturedText = cachingWriter.ToString();
						filterContext.HttpContext.Response.Write(capturedText);

						// Only cache output if this wasn't an error
						if (!wasException)
						{
							ChildActionCacheInternal.Add(uniqueId, capturedText, DateTimeOffset.UtcNow.AddSeconds(Duration));
						}
					});
				}
			}
		}

		public void OnException(ExceptionContext filterContext)
		{
			if (filterContext == null)
			{
				throw new ArgumentNullException("filterContext");
			}

			if (filterContext.IsChildAction)
			{
				CompleteChildAction(filterContext, true);
			}
		}

		public override void OnResultExecuting(ResultExecutingContext filterContext)
		{
            //Immediatly return if cache is disabled from admin
            if (!SettingsHelper.OutputCacheEnabled)
                return;

			if (filterContext == null)
			{
				throw new ArgumentNullException("filterContext");
			}

			if (!filterContext.IsChildAction)
			{
				// we need to call ProcessRequest() since there's no other way to set the Page.Response intrinsic
				using (var page = new OutputCachedPage(_cacheSettings))
				{
					page.ProcessRequest(HttpContext.Current);
				}
			}
		}

		public override void OnResultExecuted(ResultExecutedContext filterContext)
		{
            //Immediatly return if cache is disabled from admin
            if (!SettingsHelper.OutputCacheEnabled)
                return;

			if (filterContext == null)
			{
				throw new ArgumentNullException("filterContext");
			}

			if (filterContext.IsChildAction)
			{
				CompleteChildAction(filterContext, filterContext.Exception != null);
			}
		}

		private static void SetChildActionFilterFinishCallback(ControllerContext controllerContext, Action<bool> callback)
		{
			controllerContext.HttpContext.Items[ChildActionFilterFinishCallbackKey] = callback;
		}

		private static IEnumerable<string> SplitVaryByParam(string varyByParam)
		{
			if (String.Equals(varyByParam, "none", StringComparison.OrdinalIgnoreCase))
			{  // Vary by nothing
				return Enumerable.Empty<string>();
			}

			if (String.Equals(varyByParam, "*", StringComparison.OrdinalIgnoreCase))
			{  // Vary by everything
				return null;
			}

			return from part in varyByParam.Split(';')  // Vary by specific parameters
				   let trimmed = part.Trim()
				   where !String.IsNullOrEmpty(trimmed)
				   select trimmed;
		}

		private void ValidateChildActionConfiguration()
		{

			if (!String.IsNullOrWhiteSpace(CacheProfile))
			{
                if (CacheConfig != null)
				{
                    if (!string.IsNullOrWhiteSpace(CacheConfig.SqlDependency) ||
                        !string.IsNullOrWhiteSpace(CacheConfig.VaryByContentEncoding) ||
                        !string.IsNullOrWhiteSpace(CacheConfig.VaryByControl) ||
                        !string.IsNullOrWhiteSpace(CacheConfig.VaryByCustom) || CacheConfig.NoStore)
					{
						throw new InvalidOperationException("OutputCacheAttribute ChildAction UnsupportedSetting");
					}
					//overwrite the parameters
                    VaryByParam = CacheConfig.VaryByParam;
                    Duration = CacheConfig.Duration;
				}
				else
				{
					throw new HttpException("Cache settings and profile not found");
				}
			}

			if (!String.IsNullOrWhiteSpace(SqlDependency) ||
				!String.IsNullOrWhiteSpace(VaryByContentEncoding) ||
				!String.IsNullOrWhiteSpace(VaryByHeader) ||
				_locationWasSet || _noStoreWasSet)
			{
				throw new InvalidOperationException("OutputCacheAttribute ChildAction UnsupportedSetting");
			}

			if (Duration <= 0)
			{
				throw new InvalidOperationException("Invalid Duration");
			}

			if (String.IsNullOrWhiteSpace(VaryByParam))
			{
				throw new InvalidOperationException("Invalid vary by param");
			}


		}

		private sealed class OutputCachedPage : Page
		{
			private readonly OutputCacheParameters _cacheSettings;

			public OutputCachedPage(OutputCacheParameters cacheSettings)
			{
				// Tracing requires Page IDs to be unique.
				ID = Guid.NewGuid().ToString();
				_cacheSettings = cacheSettings;
			}

			protected override void FrameworkInitialize()
			{
				// when you put the <%@ OutputCache %> directive on a page, the generated code calls InitOutputCache() from here
				base.FrameworkInitialize();
				InitOutputCache(_cacheSettings);
			}
		}
	}

	internal static class MyDescriptorUtil
	{

		private static void AppendPartToUniqueIdBuilder(StringBuilder builder, object part)
		{
			if (part == null)
			{
				builder.Append("[-1]");
			}
			else
			{
				string partString = Convert.ToString(part, CultureInfo.InvariantCulture);
				builder.AppendFormat("[{0}]{1}", partString.Length, partString);
			}
		}

		public static string CreateUniqueId(params object[] parts)
		{
			return CreateUniqueId((IEnumerable<object>)parts);
		}

		public static string CreateUniqueId(IEnumerable<object> parts)
		{
			// returns a unique string made up of the pieces passed in
			var builder = new StringBuilder();
			foreach (object part in parts)
			{
				// We can special-case certain part types

				var memberInfo = part as MemberInfo;
				if (memberInfo != null)
				{
					AppendPartToUniqueIdBuilder(builder, memberInfo.Module.ModuleVersionId);
					AppendPartToUniqueIdBuilder(builder, memberInfo.MetadataToken);
					continue;
				}

				var uniquelyIdentifiable = part as ICustomUniquelyIdentifiable;
				if (uniquelyIdentifiable != null)
				{
					AppendPartToUniqueIdBuilder(builder, uniquelyIdentifiable.UniqueId);
					continue;
				}

				AppendPartToUniqueIdBuilder(builder, part);
			}

			return builder.ToString();
		}

		public static TDescriptor[] LazilyFetchOrCreateDescriptors<TReflection, TDescriptor>(ref TDescriptor[] cacheLocation, Func<TReflection[]> initializer, Func<TReflection, TDescriptor> converter)
		{
			// did we already calculate this once?
			TDescriptor[] existingCache = Interlocked.CompareExchange(ref cacheLocation, null, null);
			if (existingCache != null)
			{
				return existingCache;
			}

			var memberInfos = initializer();
			var descriptors = memberInfos.Select(converter).Where(descriptor => descriptor != null).ToArray();
			var updatedCache = Interlocked.CompareExchange(ref cacheLocation, descriptors, null);
			return updatedCache ?? descriptors;
		}

	}

	internal interface ICustomUniquelyIdentifiable
	{
		string UniqueId { get; }
	}
}