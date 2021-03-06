﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FubuCore;
using FubuCore.Reflection;
using FubuMVC.Core.Registration.Nodes;
using FubuMVC.Swank.Description;
using FubuMVC.Swank.Extensions;

namespace FubuMVC.Swank.Specification
{
    public class SpecificationService : ISpecificationService
    {
        private class ActionMapping
        {
            public ActionCall Action { get; set; }
            public ModuleDescription Module { get; set; }
            public ResourceDescription Resource { get; set; }
        }

        public static readonly Func<string, int> HttpVerbRank = x => { switch (x.IsEmpty() ? null : x.ToLower()) 
            { case "get": return 0; case "post": return 1; case "put": return 2; 
              case "update": return 3; case "delete": return 5; default: return 4; } };

        private readonly Configuration _configuration;
        private readonly ActionSource _actions;
        private readonly ITypeDescriptorCache _typeCache;
        private readonly IDescriptionConvention<ActionCall, ModuleDescription> _moduleConvention;
        private readonly IDescriptionConvention<ActionCall, ResourceDescription> _resourceConvention;
        private readonly IDescriptionConvention<ActionCall, EndpointDescription> _endpointConvention;
        private readonly IDescriptionConvention<PropertyInfo, MemberDescription> _memberConvention;
        private readonly IDescriptionConvention<FieldInfo, OptionDescription> _optionConvention;
        private readonly IDescriptionConvention<ActionCall, List<StatusCodeDescription>> _statusCodeConvention;
        private readonly IDescriptionConvention<ActionCall, List<HeaderDescription>> _headerConvention;
        private readonly IDescriptionConvention<System.Type, TypeDescription> _typeConvention;
        private readonly MergeService _mergeService;

        public SpecificationService(
            Configuration configuration, 
            ActionSource actions,
            ITypeDescriptorCache typeCache,
            IDescriptionConvention<ActionCall, ModuleDescription> moduleConvention,
            IDescriptionConvention<ActionCall, ResourceDescription> resourceConvention,
            IDescriptionConvention<ActionCall, EndpointDescription> endpointConvention,
            IDescriptionConvention<PropertyInfo, MemberDescription> memberConvention,
            IDescriptionConvention<FieldInfo, OptionDescription> optionConvention,
            IDescriptionConvention<ActionCall, List<StatusCodeDescription>> statusCodeConvention, 
            IDescriptionConvention<ActionCall, List<HeaderDescription>> headerConvention,
            IDescriptionConvention<System.Type, TypeDescription> typeConvention,
            MergeService mergeService)
        {
            _configuration = configuration;
            _actions = actions;
            _typeCache = typeCache;
            _moduleConvention = moduleConvention;
            _resourceConvention = resourceConvention;
            _endpointConvention = endpointConvention;
            _memberConvention = memberConvention;
            _optionConvention = optionConvention;
            _statusCodeConvention = statusCodeConvention;
            _typeConvention = typeConvention;
            _mergeService = mergeService;
            _headerConvention = headerConvention;
        }

        public Specification Generate()
        {
            var actionMapping = GetActionMapping(_actions.GetActions());
            CheckForOrphanedActions(actionMapping);
            var specification = new Specification {
                    Name = _configuration.Name,
                    Comments = _configuration.AppliesToAssemblies
                        .Select(x => x.FindTextResourceNamed("*" + _configuration.Comments))
                        .FirstOrDefault(x => x != null),
                    Types = GetTypes(actionMapping.Select(x => x.Action).ToList()),
                    Modules = GetModules(actionMapping.Where(x => x.Module != null).ToList()),
                    Resources = GetResources(actionMapping.Where(x => x.Module == null).ToList())
                };
            if (_configuration.MergeSpecificationPath.IsNotEmpty())
                specification = _mergeService.Merge(specification, _configuration.MergeSpecificationPath);
            return specification;
        }

        private List<ActionMapping> GetActionMapping(IEnumerable<ActionCall> actions)
        {
            return actions
                .Where(x => !x.Method.HasAttribute<HideAttribute>() && !x.HandlerType.HasAttribute<HideAttribute>())
                .Select(x => new { Action = x, Module = _moduleConvention.GetDescription(x), Resource = _resourceConvention.GetDescription(x) })
                .Where(x => ((_configuration.OrphanedModuleActions == OrphanedActions.Exclude && x.Module != null) ||
                              _configuration.OrphanedModuleActions != OrphanedActions.Exclude))
                .Where(x => ((_configuration.OrphanedResourceActions == OrphanedActions.Exclude && x.Resource != null) ||
                              _configuration.OrphanedResourceActions != OrphanedActions.Exclude))
                .Select(x => new ActionMapping {
                    Action = x.Action,
                    Module = _configuration.OrphanedModuleActions == OrphanedActions.UseDefault ?
                        x.Module ?? _configuration.DefaultModuleFactory(x.Action) : x.Module,
                    Resource = _configuration.OrphanedResourceActions == OrphanedActions.UseDefault ?
                        x.Resource ?? _configuration.DefaultResourceFactory(x.Action) : x.Resource
                }).ToList();
        }

        private void CheckForOrphanedActions(IList<ActionMapping> actionMapping)
        {
            if (_configuration.OrphanedModuleActions == OrphanedActions.Fail)
            {
                var orphanedModuleActions = actionMapping.Where(x => x.Module == null).ToList();
                if (orphanedModuleActions.Any()) throw new OrphanedModuleActionException(
                    orphanedModuleActions.Select(x => x.Action.HandlerType.FullName + "." + x.Action.Method.Name));
            }

            if (_configuration.OrphanedResourceActions == OrphanedActions.Fail)
            {
                var orphanedActions = actionMapping.Where(x => x.Resource == null).ToList();
                if (orphanedActions.Any()) throw new OrphanedResourceActionException(
                    orphanedActions.Select(x => x.Action.HandlerType.FullName + "." + x.Action.Method.Name));
            }
        }

        private List<Type> GetTypes(IList<ActionCall> actions)
        {
            var rootTypes = actions
                .Where(x => x.HasInput && !x.ParentChain().Route.AllowsGet() && !x.ParentChain().Route.AllowsDelete())
                .Select(x => new TypeContext(x.InputType().GetListElementType() ?? x.InputType(), null, x))
                .Concat(actions.Where(x => x.HasOutput)
                               .SelectDistinct(x => x.OutputType())
                               .Select(x => new TypeContext(x.GetListElementType() ?? x)))
                .ToList();
            return rootTypes
                .Concat(rootTypes.SelectMany(GetTypes))
                .DistinctBy(x => x.Type, x => x.Action)
                .Select(x => {
                    var description = _typeConvention.GetDescription(x.Type);
                    var type = description.WhenNotNull(y => y.Type).Otherwise(x.Type);
                    return _configuration.TypeOverrides.Apply(x.Type, new Type {
                        Id = x.Action != null ? _configuration.InputTypeIdConvention(type, x.Action.Method) : _configuration.TypeIdConvention(type),
                        Name = description.WhenNotNull(y => y.Name).OtherwiseDefault(),
                        Comments = description.WhenNotNull(y => y.Comments).OtherwiseDefault(),
                        Members = GetMembers(type, x.Action)
                    });
                })
                .OrderBy(x => x.Name).ToList();
        }

        private List<TypeContext> GetTypes(TypeContext type)
        {
            var properties = type.Type.IsProjection() ?
                type.Type.GetProjectionProperties() :
                _typeCache.GetPropertiesFor(type.Type).Select(x => x.Value);
            var types = properties
                .Where(x => !x.IsHidden() &&
                            !(x.PropertyType.GetListElementType() ?? x.PropertyType).IsSystemType() && 
                            !x.PropertyType.IsEnum &&
                            !x.IsAutoBound())
                .Select(x => new TypeContext(x.PropertyType.GetListElementType() ?? x.PropertyType, type))
                .Distinct()
                .ToList();
            return types.Concat(types
                            .Where(x => type.Traverse(y => y.Parent).All(y => y.Type != x.Type))
                            .SelectMany(GetTypes))
                        .Distinct()
                        .ToList();
        }

        private List<Member> GetMembers(System.Type type, ActionCall action)
        {
            var properties = type.IsProjection() ? 
                type.GetProjectionProperties() : 
                _typeCache.GetPropertiesFor(type).Select(x => x.Value);
            return properties
                .Where(x => !x.IsHidden() &&
                            !x.IsAutoBound() &&
                            !x.IsQuerystring(action) &&
                            !x.IsUrlParameter(action))
                .Select(x => {
                        var description = _memberConvention.GetDescription(x);
                        var memberType = description.WhenNotNull(y => y.Type).Otherwise(x.PropertyType.GetListElementType(), x.PropertyType);
                        return _configuration.MemberOverrides.Apply(x, new Member {
                            Name = description.WhenNotNull(y => y.Name).OtherwiseDefault(),
                            Comments = description.WhenNotNull(y => y.Comments).OtherwiseDefault(),
                            DefaultValue = description.WhenNotNull(y => y.DefaultValue).WhenNotNull(z => z.ToDefaultValueString(_configuration)).OtherwiseDefault(),
                            Required = description.WhenNotNull(y => y.Required).OtherwiseDefault(),
                            Type = memberType.IsSystemType() || memberType.IsEnum ? memberType.GetXmlName() : _configuration.TypeIdConvention(memberType),
                            IsArray = x.PropertyType.IsArray || x.PropertyType.IsList(),
                            ArrayItemName = description.WhenNotNull(y => y.ArrayItemName).OtherwiseDefault(),
                            Options = GetOptions(x.PropertyType)
                        });
                    })
                .ToList();
        } 

        private List<Module> GetModules(IEnumerable<ActionMapping> actionMapping)
        {
            return actionMapping
                .GroupBy(x => x.Module)
                .Select(x => _configuration.ModuleOverrides.Apply(new Module {
                    Name = x.Key.Name,
                    Comments = x.Key.Comments,
                    Resources = GetResources(x.Select(y => y).ToList())
                }))
                .OrderBy(x => x.Name).ToList();
        }

        private List<Resource> GetResources(IEnumerable<ActionMapping> actionMapping)
        {
            return actionMapping
                .GroupBy(x => x.Resource)
                .Select(x => _configuration.ResourceOverrides.Apply(new Resource {
                    Name = x.Key.Name,
                    Comments = x.Key.Comments ?? x.First().Action.HandlerType.Assembly
                        .FindTextResourceNamed(x.First().Action.HandlerType.Namespace + ".resource"),
                    Endpoints = GetEndpoints(x.Select(y => y.Action))
                }))
                .OrderBy(x => x.Name).ToList();
        }

        private List<Endpoint> GetEndpoints(IEnumerable<ActionCall> actions)
        {
            return actions
                .Select(x => {
                    var endpoint = _endpointConvention.GetDescription(x);
                    var route = x.ParentChain().Route;
                    var querystring = x.HasInput ? GetQuerystringParameters(x) : null;
                    return _configuration.EndpointOverrides.Apply(x, new Endpoint {
                        Name = endpoint.WhenNotNull(y => y.Name).OtherwiseDefault(),
                        Comments = endpoint.WhenNotNull(y => y.Comments).OtherwiseDefault(),
                        Url = route.Pattern.EnusureStartsWith("/") + querystring.Join(y => "{0}={{{0}}}".ToFormat(y.Name), "?", "&", ""),
                        Method = route.AllowedHttpMethods.FirstOrDefault(),
                        UrlParameters = x.HasInput ? GetUrlParameters(x) : null,
                        QuerystringParameters = querystring,
                        StatusCodes = GetStatusCodes(x),
                        Headers = GetHeaders(x),
                        Request = x.HasInput && (route.AllowsPost() || route.AllowsPut()) ? 
                            _configuration.RequestOverrides.Apply(x, GetData(x.InputType(), endpoint.RequestComments, x.Method)) : null,
                        Response = x.HasOutput ? 
                            _configuration.ResponseOverrides.Apply(x, GetData(x.OutputType(), endpoint.ResponseComments)) : null
                    });
                }).OrderBy(x => x.Url.Split('?').First()).ThenBy(x => HttpVerbRank(x.Method)).ToList();
        }

        private List<UrlParameter> GetUrlParameters(ActionCall action)
        {
            var properties = _typeCache.GetPropertiesFor(action.InputType());
            return action.ParentChain().Route.Input.RouteParameters.Select(
                x => {
                    var property = properties[x.Name];
                    var description = _memberConvention.GetDescription(property);
                    return _configuration.UrlParameterOverrides.Apply(action, property, new UrlParameter {
                            Name = description.WhenNotNull(y => y.Name).OtherwiseDefault(),
                            Comments = description.WhenNotNull(y => y.Comments).OtherwiseDefault(),
                            Type = property.PropertyType.GetXmlName(),
                            Options = GetOptions(property.PropertyType)
                        });
                }).ToList();
        }

        private List<QuerystringParameter> GetQuerystringParameters(ActionCall action)
        {
            return _typeCache.GetPropertiesFor(action.InputType())
                .Where(x => x.Value.IsQuerystring(action) && 
                            !x.Value.HasAttribute<HideAttribute>() && 
                            !x.Value.IsAutoBound())
                .Select(x => {
                    var description = _memberConvention.GetDescription(x.Value);
                    return _configuration.QuerystringOverrides.Apply(action, x.Value, new QuerystringParameter {
                        Name = description.WhenNotNull(y => y.Name).OtherwiseDefault(),
                        Comments = description.WhenNotNull(y => y.Comments).OtherwiseDefault(),
                        Type = (x.Value.PropertyType.GetListElementType() ?? x.Value.PropertyType).GetXmlName(),
                        Options = GetOptions(x.Value.PropertyType),
                        DefaultValue = description.DefaultValue.WhenNotNull(y => y.ToDefaultValueString(_configuration)).OtherwiseDefault(),
                        MultipleAllowed = x.Value.PropertyType.IsArray || x.Value.PropertyType.IsList(),
                        Required = description.Required
                    });
                }).OrderBy(x => x.Name).ToList();
        }

        private List<StatusCode> GetStatusCodes(ActionCall action)
        {
            return _statusCodeConvention.GetDescription(action)
                .Select(x => _configuration.StatusCodeOverrides.Apply(action, new StatusCode {
                    Code = x.Code,
                    Name = x.Name,
                    Comments = x.Comments
                })).OrderBy(x => x.Code).ToList();
        }

        private List<Header> GetHeaders(ActionCall action)
        {
            return _headerConvention.GetDescription(action)
                .Select(x => _configuration.HeaderOverrides.Apply(action, new Header {
                    Type = x.Type.ToString(),
                    Name = x.Name,
                    Comments = x.Comments,
                    Optional = x.Optional
                })).OrderBy(x => x.Type).ThenBy(x => x.Name).ToList();
        }

        private Data GetData(System.Type type, string comments, MethodInfo action = null)
        {
            var dataType = _typeConvention.GetDescription(type);
            var rootType = dataType.WhenNotNull(x => x.Type).Otherwise(type);
            return new Data
                {
                    Name = dataType.WhenNotNull(y => y.Name).OtherwiseDefault(),
                    Comments = comments,
                    Type = action.WhenNotNull(x => _configuration.InputTypeIdConvention(rootType, x))
                            .Otherwise(_configuration.TypeIdConvention(rootType)),
                    IsArray = type.IsArray || type.IsList()
                };
        } 

        private List<Option> GetOptions(System.Type type)
        {
            return type.IsEnum || (type.IsNullable() && Nullable.GetUnderlyingType(type).IsEnum) ? 
                type.GetEnumOptions()
                    .Where(x => !x.HasAttribute<HideAttribute>())
                    .Select(x => {
                        var option = _optionConvention.GetDescription(x);
                        return _configuration.OptionOverrides.Apply(x, new Option {
                            Name = option.WhenNotNull(y => y.Name).OtherwiseDefault(),
                            Comments = option.WhenNotNull(y => y.Comments).OtherwiseDefault(), 
                            Value = _configuration.EnumValue == EnumValue.AsString ? x.Name : x.GetRawConstantValue().ToString()
                        });
                    }).OrderBy(x => x.Name ?? x.Value).ToList()
             : new List<Option>();
        }
    }
}
