// Copyright 2016-2021, Pulumi Corporation

using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;

namespace Pulumi
{
    public partial class Deployment
    {
        private async Task<PrepareResult> PrepareResourceAsync(
            string label, Resource res, bool custom, bool remote,
            ResourceArgs args, ResourceOptions options)
        {
            // Before we can proceed, all our dependencies must be finished.
            var type = res.GetResourceType();
            var name = res.GetResourceName();

            LogExcessive($"Gathering explicit dependencies: t={type}, name={name}, custom={custom}, remote={remote}");
            var explicitDirectDependencies = new HashSet<Resource>(
                await GatherExplicitDependenciesAsync(options.DependsOn).ConfigureAwait(false));
            LogExcessive($"Gathered explicit dependencies: t={type}, name={name}, custom={custom}, remote={remote}");

            // Serialize out all our props to their final values.  In doing so, we'll also collect all
            // the Resources pointed to by any Dependency objects we encounter, adding them to 'propertyDependencies'.
            LogExcessive($"Serializing properties: t={type}, name={name}, custom={custom}, remote={remote}");
            var dictionary = await args.ToDictionaryAsync().ConfigureAwait(false);
            var (serializedProps, propertyToDirectDependencies) =
                await SerializeResourcePropertiesAsync(
                        label,
                        dictionary,
                        await this.MonitorSupportsResourceReferences().ConfigureAwait(false),
                        keepOutputValues: remote && await MonitorSupportsOutputValues().ConfigureAwait(false)).ConfigureAwait(false);
            LogExcessive($"Serialized properties: t={type}, name={name}, custom={custom}, remote={remote}");

            // Wait for the parent to complete.
            // If no parent was provided, parent to the root resource.
            LogExcessive($"Getting parent urn: t={type}, name={name}, custom={custom}, remote={remote}");
            var parentUrn = options.Parent != null
                ? await options.Parent.Urn.GetValueAsync(whenUnknown: default!).ConfigureAwait(false)
                : await GetRootResourceAsync(type).ConfigureAwait(false);
            LogExcessive($"Got parent urn: t={type}, name={name}, custom={custom}, remote={remote}");

            string? providerRef = null;
            if (custom)
            {
                var customOpts = options as CustomResourceOptions;
                providerRef = await ProviderResource.RegisterAsync(customOpts?.Provider).ConfigureAwait(false);

                // Note: because of the hard distinction between custom and
                // component resources, we don't fully mirror the behavior found
                // in other SDKs. Because custom resources are passed with
                // CustomResourceOptions, there is no Providers list. This makes
                // it nonsensical to find a candidate for Provider in Providers.

                if (providerRef == null)
                {
                    var t = res.GetResourceType();
                    var parentRef = customOpts?.Parent?.GetProvider(t);
                    providerRef = await ProviderResource.RegisterAsync(parentRef).ConfigureAwait(false);
                }
            }

            var providerRefs = new Dictionary<string, string>();
            if (remote && options is ComponentResourceOptions componentOpts)
            {
                if (componentOpts.Provider != null)
                {
                    var duplicate = false;
                    foreach (var p in componentOpts.Providers)
                    {
                        if (p.Package == componentOpts.Provider.Package)
                        {
                            duplicate = true;
                            await _logger.WarnAsync(
                                $"Conflict between provider and providers field for package '{p.Package}'. " +
                                "This behavior is depreciated, and will turn into an error July 2022. " +
                                "For more information, see https://github.com/pulumi/pulumi/issues/8799.", res)
                                .ConfigureAwait(false);
                        }
                    }
                    if (!duplicate)
                    {
                        componentOpts.Providers.Add(componentOpts.Provider);
                    }
                }

                foreach (var provider in componentOpts.Providers)
                {
                    var pref = await ProviderResource.RegisterAsync(provider).ConfigureAwait(false);
                    if (pref != null)
                    {
                        providerRefs.Add(provider.Package, pref);
                    }
                }
            }

            // Collect the URNs for explicit/implicit dependencies for the engine so that it can understand
            // the dependency graph and optimize operations accordingly.

            // The list of all dependencies (implicit or explicit).
            var allDirectDependencies = new HashSet<Resource>(explicitDirectDependencies);

            var allDirectDependencyUrns = await GetAllTransitivelyReferencedResourceUrnsAsync(explicitDirectDependencies).ConfigureAwait(false);
            var propertyToDirectDependencyUrns = new Dictionary<string, HashSet<string>>();

            foreach (var (propertyName, directDependencies) in propertyToDirectDependencies)
            {
                allDirectDependencies.AddRange(directDependencies);

                var urns = await GetAllTransitivelyReferencedResourceUrnsAsync(directDependencies).ConfigureAwait(false);
                allDirectDependencyUrns.AddRange(urns);
                propertyToDirectDependencyUrns[propertyName] = urns;
            }

            var resourceMonitorSupportsAliasSpecs = await MonitorSupportsAliasSpecs().ConfigureAwait(false);
            var aliases = await PrepareAliases(res, options, resourceMonitorSupportsAliasSpecs).ConfigureAwait(false);

            return new PrepareResult(
                serializedProps,
                parentUrn ?? "",
                providerRef ?? "",
                providerRefs,
                allDirectDependencyUrns,
                propertyToDirectDependencyUrns,
                aliases,
                resourceMonitorSupportsAliasSpecs);

            void LogExcessive(string message)
            {
                if (_excessiveDebugOutput)
                    Log.Debug(message);
            }
        }

        static async Task<T> Resolve<T>(Input<T>? input, T whenUnknown)
        {
            return input == null
                ? whenUnknown
                : await input.ToOutput().GetValueAsync(whenUnknown).ConfigureAwait(false);
        }

        private static async Task<List<Pulumirpc.Alias>> PrepareAliases(
            Resource resource,
            ResourceOptions options,
            bool resourceMonitorSupportsAliasSpec)
        {
            var aliases = new List<Pulumirpc.Alias>();
            if (resourceMonitorSupportsAliasSpec)
            {
                // map each alias in the options to its corresponding Alias proto definition.
                foreach (var alias in options.Aliases)
                {
                    var defaultAliasWhenUnknown = new Alias();
                    var resolvedAlias = await alias.ToOutput().GetValueAsync(whenUnknown: defaultAliasWhenUnknown).ConfigureAwait(false);
                    if (ReferenceEquals(resolvedAlias, defaultAliasWhenUnknown))
                    {
                        // alias contains unknowns, skip it.
                        continue;
                    }

                    if (resolvedAlias.Urn != null)
                    {
                        // Alias URN fully provided, use it as is
                        aliases.Add(new Pulumirpc.Alias
                        {
                            Urn = resolvedAlias.Urn
                        });

                        continue;
                    }

                    var aliasSpec = new Pulumirpc.Alias.Types.Spec
                    {
                        Name = await Resolve(resolvedAlias.Name, ""),
                        Type = await Resolve(resolvedAlias.Type, ""),
                        Stack = await Resolve(resolvedAlias.Stack, ""),
                        Project = await Resolve(resolvedAlias.Project, ""),
                    };

                    // Here we specify whether the alias has a parent or not.
                    // aliasSpec must only specify one of NoParent or ParentUrn, not both!
                    // this determines the wire format of the alias which is used by the engine.
                    if (resolvedAlias.Parent == null && resolvedAlias.ParentUrn == null)
                    {
                        aliasSpec.NoParent = resolvedAlias.NoParent;
                    }
                    else if (resolvedAlias.Parent != null)
                    {
                        var aliasParentUrn = await Resolve(resolvedAlias.Parent.Urn, "");
                        if (!string.IsNullOrEmpty(aliasParentUrn))
                        {
                            aliasSpec.ParentUrn = aliasParentUrn;
                        }
                    }
                    else
                    {
                        var aliasParentUrn = await Resolve(resolvedAlias.ParentUrn, "");
                        if (!string.IsNullOrEmpty(aliasParentUrn))
                        {
                            aliasSpec.ParentUrn = aliasParentUrn;
                        }
                    }

                    aliases.Add(new Pulumirpc.Alias
                    {
                        Spec = aliasSpec
                    });
                }
            }
            else
            {
                // If the engine does not support alias specs, we fall back to the old behavior of
                // collapsing all aliases into urns.
                var uniqueAliases = new HashSet<string>();
                var allAliases = AllAliases(
                    options.Aliases,
                    resource.GetResourceName(),
                    resource.GetResourceType(),
                    options.Parent);
                foreach (var aliasUrn in allAliases)
                {
                    var aliasValue = await Resolve(aliasUrn, "");
                    if (!string.IsNullOrEmpty(aliasValue) && uniqueAliases.Add(aliasValue))
                    {
                        aliases.Add(new Pulumirpc.Alias
                        {
                            Urn = aliasValue
                        });
                    }
                }
            }

            return aliases;
        }

        /// <summary>
        /// <see cref="AllAliases"/> makes a copy of the aliases array, and add to it any
        /// implicit aliases inherited from its parent. If there are N child aliases, and
        /// M parent aliases, there will be (M+1)*(N+1)-1 total aliases, or, as calculated
        /// in the logic below, N+(M*(1+N)).
        /// </summary>
        internal static ImmutableArray<Input<string>> AllAliases(List<Input<Alias>> childAliases, string childName, string childType, Resource? parent)
        {
            var aliases = ImmutableArray.CreateBuilder<Input<string>>();
            foreach (var childAlias in childAliases)
            {
                aliases.Add(CollapseAliasToUrn(childAlias, childName, childType, parent));
            }
            if (parent != null)
            {
                foreach (var parentAlias in parent._aliases)
                {
                    var parentName = parent.GetResourceName();
                    aliases.Add(Pulumi.Urn.InheritedChildAlias(childName, parentName, parentAlias, childType));
                    foreach (var childAlias in childAliases)
                    {
                        var inheritedAlias = CollapseAliasToUrn(childAlias, childName, childType, parent).Apply(childAliasURN =>
                        {
                            var aliasedChildName = Pulumi.Urn.Name(childAliasURN);
                            var aliasedChildType = Pulumi.Urn.Type(childAliasURN);
                            return Pulumi.Urn.InheritedChildAlias(aliasedChildName, parentName, parentAlias, aliasedChildType);
                        });
                        aliases.Add(inheritedAlias);
                    }
                }
            }
            return aliases.ToImmutable();
        }

        internal static Output<string> CollapseAliasToUrn(
            Input<Alias> alias,
            string defaultName,
            string defaultType,
            Resource? defaultParent)
        {
            return alias.ToOutput().Apply(a =>
            {
                if (a.Urn != null)
                {
                    CheckNull(a.Name, nameof(a.Name));
                    CheckNull(a.Type, nameof(a.Type));
                    CheckNull(a.Project, nameof(a.Project));
                    CheckNull(a.Stack, nameof(a.Stack));
                    CheckNull(a.Parent, nameof(a.Parent));
                    CheckNull(a.ParentUrn, nameof(a.ParentUrn));
                    if (a.NoParent)
                        ThrowAliasPropertyConflict(nameof(a.NoParent));

                    return Output.Create(a.Urn);
                }

                var name = a.Name ?? defaultName;
                var type = a.Type ?? defaultType;
                var project = a.Project ?? Deployment.Instance.ProjectName;
                var stack = a.Stack ?? Deployment.Instance.StackName;

                var parentCount =
                    (a.Parent != null ? 1 : 0) +
                    (a.ParentUrn != null ? 1 : 0) +
                    (a.NoParent ? 1 : 0);

                if (parentCount >= 2)
                {
                    throw new ArgumentException(
$"Only specify one of '{nameof(Alias.Parent)}', '{nameof(Alias.ParentUrn)}' or '{nameof(Alias.NoParent)}' in an {nameof(Alias)}");
                }

                var (parent, parentUrn) = GetParentInfo(defaultParent, a);

                if (name == null)
                    throw new ArgumentNullException("No valid 'Name' passed in for alias.");

                if (type == null)
                    throw new ArgumentNullException("No valid 'type' passed in for alias.");

                return Pulumi.Urn.Create(name, type, parent, parentUrn, project, stack);
            });
        }


        private static void CheckNull<T>(T? value, string name) where T : class
        {
            if (value != null)
            {
                ThrowAliasPropertyConflict(name);
            }
        }

        private static void ThrowAliasPropertyConflict(string name)
            => throw new ArgumentException($"{nameof(Alias)} should not specify both {nameof(Alias.Urn)} and {name}");

        private static (Resource? parent, Input<string>? urn) GetParentInfo(Resource? defaultParent, Alias alias)
        {
            if (alias.Parent != null)
                return (alias.Parent, null);

            if (alias.ParentUrn != null)
                return (null, alias.ParentUrn);

            if (alias.NoParent)
                return (null, null);

            return (defaultParent, null);
        }


        private static Task<ImmutableArray<Resource>> GatherExplicitDependenciesAsync(InputList<Resource> resources)
            => resources.ToOutput().GetValueAsync(whenUnknown: ImmutableArray<Resource>.Empty);

        internal static async Task<HashSet<string>> GetAllTransitivelyReferencedResourceUrnsAsync(
            HashSet<Resource> resources)
        {
            // Go through 'resources', but transitively walk through **Component** resources, collecting any
            // of their child resources.  This way, a Component acts as an aggregation really of all the
            // reachable resources it parents.  This walking will stop when it hits custom resources.
            //
            // This function also terminates at remote components, whose children are not known to the Node SDK directly.
            // Remote components will always wait on all of their children, so ensuring we return the remote component
            // itself here and waiting on it will accomplish waiting on all of it's children regardless of whether they
            // are returned explicitly here.
            //
            // In other words, if we had:
            //
            //                  Comp1
            //              /     |     \
            //          Cust1   Comp2  Remote1
            //                  /   \       \
            //              Cust2   Cust3  Comp3
            //              /                 \
            //          Cust4                Cust5
            //
            // Then the transitively reachable resources of Comp1 will be [Cust1, Cust2, Cust3, Remote1]. It
            // will *not* include:
            // * Cust4 because it is a child of a custom resource
            // * Comp2 because it is a non-remote component resoruce
            // * Comp3 and Cust5 because Comp3 is a child of a remote component resource
            var transitivelyReachableResources = GetTransitivelyReferencedChildResourcesOfComponentResources(resources);

            var transitivelyReachableCustomResources = transitivelyReachableResources.Where(res =>
            {
                switch (res)
                {
                    case CustomResource _: return true;
                    case ComponentResource component: return component.remote;
                    default: return false; // Unreachable
                }
            });
            var tasks = transitivelyReachableCustomResources.Select(r => r.Urn.GetValueAsync(whenUnknown: ""));
            var urns = await Task.WhenAll(tasks).ConfigureAwait(false);
            return new HashSet<string>(urns.Where(urn => !string.IsNullOrEmpty(urn)));
        }

        /// <summary>
        /// Recursively walk the resources passed in, returning them and all resources reachable from
        /// <see cref="Resource.ChildResources"/> through any **Component** resources we encounter.
        /// </summary>
        private static HashSet<Resource> GetTransitivelyReferencedChildResourcesOfComponentResources(HashSet<Resource> resources)
        {
            // Recursively walk the dependent resources through their children, adding them to the result set.
            var result = new HashSet<Resource>();
            AddTransitivelyReferencedChildResourcesOfComponentResources(resources, result);
            return result;
        }

        private static void AddTransitivelyReferencedChildResourcesOfComponentResources(HashSet<Resource> resources, HashSet<Resource> result)
        {
            foreach (var resource in resources)
            {
                if (result.Add(resource))
                {
                    if (resource is ComponentResource)
                    {
                        HashSet<Resource> childResources;
                        lock (resource.ChildResources)
                        {
                            childResources = new HashSet<Resource>(resource.ChildResources);
                        }
                        AddTransitivelyReferencedChildResourcesOfComponentResources(childResources, result);
                    }
                }
            }
        }

        private readonly struct PrepareResult
        {
            public readonly Struct SerializedProps;
            public readonly string ParentUrn;
            public readonly string ProviderRef;
            public readonly Dictionary<string, string> ProviderRefs;
            public readonly HashSet<string> AllDirectDependencyUrns;
            public readonly Dictionary<string, HashSet<string>> PropertyToDirectDependencyUrns;
            public readonly List<Pulumirpc.Alias> Aliases;
            /// <summary>
            /// Returns whether the resource monitor we are connected to supports the "aliasSpec" feature across the RPC interface.
            /// When that is not the case, use only use the URNs of the aliases to populate the AliasURNs field of RegisterResourceRequest,
            /// otherwise we pass the full structure of the Aliases field to the resource monitor.
            /// </summary>
            public readonly bool SupportsAliasSpec;

            public PrepareResult(
                Struct serializedProps,
                string parentUrn,
                string providerRef,
                Dictionary<string, string> providerRefs,
                HashSet<string> allDirectDependencyUrns,
                Dictionary<string,
                HashSet<string>> propertyToDirectDependencyUrns,
                List<Pulumirpc.Alias> aliases,
                bool supportsAliasSpec)
            {
                SerializedProps = serializedProps;
                ParentUrn = parentUrn;
                ProviderRef = providerRef;
                ProviderRefs = providerRefs;
                AllDirectDependencyUrns = allDirectDependencyUrns;
                PropertyToDirectDependencyUrns = propertyToDirectDependencyUrns;
                SupportsAliasSpec = supportsAliasSpec;
                Aliases = aliases;
            }
        }
    }
}
