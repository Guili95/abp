﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Volo.Abp.Cli.Commands;
using Volo.Abp.Cli.Http;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Http.Modeling;
using Volo.Abp.Json;
using Volo.Abp.Modularity;

namespace Volo.Abp.Cli.ServiceProxy.CSharp
{
    public class CSharpServiceProxyGenerator : ServiceProxyGeneratorBase, ITransientDependency
    {
        public const string Name = "CSHARP";

        private const string UsingPlaceholder = "<using placeholder>";
        private const string MethodPlaceholder = "<method placeholder>";
        private const string ClassName = "<className>";
        private const string ServiceInterface = "<serviceInterface>";
        private const string ServicePostfix = "APPSERVICE";
        private const string DefaultNamespace = "ClientProxies";
        private const string Namespace = "<namespace>";
        private readonly string _clientProxyTemplate = "// This file is automatically generated by ABP framework to use MVC Controllers from CSharp" +
                                                       $"{Environment.NewLine}<using placeholder>" +
                                                       $"{Environment.NewLine}" +
                                                       $"{Environment.NewLine}namespace <namespace>" +
                                                       $"{Environment.NewLine}{{" +
                                                       $"{Environment.NewLine}    public partial class <className> : ClientProxyBase<<serviceInterface>>, <serviceInterface>" +
                                                       $"{Environment.NewLine}    {{" +
                                                       $"{Environment.NewLine}        <method placeholder>" +
                                                       $"{Environment.NewLine}    }}" +
                                                       $"{Environment.NewLine}}}";
        private readonly string _clientProxyPartialTemplate = "// This file is part of <className>, you can customize it here" +
                                                              $"{Environment.NewLine}namespace <namespace>" +
                                                              $"{Environment.NewLine}{{" +
                                                              $"{Environment.NewLine}    public partial class <className>" +
                                                              $"{Environment.NewLine}    {{" +
                                                              $"{Environment.NewLine}    }}" +
                                                              $"{Environment.NewLine}}}";
        private readonly List<string> _usingNamespaceList = new()
        {
            "using System;",
            "using Volo.Abp.Application.Dtos;",
            "using Volo.Abp.Http.Client;",
            "using Volo.Abp.Http.Modeling;"
        };

        public CSharpServiceProxyGenerator(
            CliHttpClientFactory cliHttpClientFactory,
            IJsonSerializer jsonSerializer) :
            base(cliHttpClientFactory, jsonSerializer)
        {

        }

        public override async Task GenerateProxyAsync(GenerateProxyArgs args)
        {
            var projectFilePath = CheckWorkDirectory(args.WorkDirectory);

            if (args.CommandName == RemoveProxyCommand.Name)
            {
                RemoveClientProxyFile(args);
                return;
            }

            var projectName = Path.GetFileNameWithoutExtension(projectFilePath);
            var assemblyFilePath = Path.Combine(args.WorkDirectory, "bin", "Debug", GetTargetFrameworkVersion(projectFilePath), $"{projectName}.dll");
            var startupModule = GetStartupModule(assemblyFilePath);

            var appServiceTypes = new List<Type>();
            FindAppServiceTypesRecursively(startupModule, appServiceTypes);
            appServiceTypes = appServiceTypes.Distinct().ToList();

            var applicationApiDescriptionModel = await GetApplicationApiDescriptionModelAsync(args);

            foreach (var controller in applicationApiDescriptionModel.Modules[args.Module].Controllers)
            {
                if (ShouldGenerateProxy(controller.Value))
                {
                    await GenerateClientProxyFileAsync(args, controller.Value, appServiceTypes, startupModule.Namespace);
                }
            }
        }

        private void RemoveClientProxyFile(GenerateProxyArgs args)
        {
            var folder = args.Folder.IsNullOrWhiteSpace()? DefaultNamespace : args.Folder;
            var folderPath = Path.Combine(args.WorkDirectory, folder);

            if (Directory.Exists(folderPath))
            {
                Directory.Delete(folderPath, true);
            }
        }

        private async Task GenerateClientProxyFileAsync(
            GenerateProxyArgs args,
            ControllerApiDescriptionModel controllerApiDescription,
            List<Type> appServiceTypes,
            string rootNamespace)
        {
            var appServiceType = appServiceTypes.FirstOrDefault(x => x.FullName == controllerApiDescription.Interfaces.Last().Type);

            if (appServiceType == null)
            {
                return;
            }

            var folder = args.Folder.IsNullOrWhiteSpace()? DefaultNamespace : args.Folder;
            var usingNamespaceList = new List<string>(_usingNamespaceList);

            var clientProxyName = $"{controllerApiDescription.ControllerName}ClientProxy";
            var clientProxyBuilder = new StringBuilder(_clientProxyTemplate);
            var fileNamespace = $"{rootNamespace}.{folder.Replace('/', '.')}";
            usingNamespaceList.Add($"using {appServiceType.Namespace};");

            clientProxyBuilder.Replace(ClassName, clientProxyName);
            clientProxyBuilder.Replace(Namespace, fileNamespace);
            clientProxyBuilder.Replace(ServiceInterface, appServiceType.Name);

            var methods = appServiceType.GetInterfaces().SelectMany(x => x.GetMethods()).ToList();
            methods.AddRange(appServiceType.GetMethods());
            foreach (var method in methods)
            {
                var actionApiDescription = controllerApiDescription.Actions.Values.FirstOrDefault(x => x.Name == method.Name);
                if (actionApiDescription == null)
                {
                    continue;
                }

                GenerateMethod(actionApiDescription, method, clientProxyBuilder, usingNamespaceList);
            }

            foreach (var usingNamespace in usingNamespaceList)
            {
                clientProxyBuilder.Replace($"{UsingPlaceholder}", $"{usingNamespace}{Environment.NewLine}{UsingPlaceholder}");
            }

            clientProxyBuilder.Replace($"{Environment.NewLine}{UsingPlaceholder}", string.Empty);
            clientProxyBuilder.Replace($"{Environment.NewLine}        {MethodPlaceholder}", string.Empty);

            var filePath = Path.Combine(args.WorkDirectory, folder, clientProxyName + ".cs");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));

            using (var writer = new StreamWriter(filePath))
            {
                await writer.WriteAsync(clientProxyBuilder.ToString());
            }

            await GenerateClientProxyPartialFileAsync(clientProxyName, fileNamespace, filePath);
        }

        private async Task GenerateClientProxyPartialFileAsync(string clientProxyName, string fileNamespace,  string filePath)
        {
            var clientProxyBuilder = new StringBuilder(_clientProxyPartialTemplate);
            clientProxyBuilder.Replace(ClassName, clientProxyName);
            clientProxyBuilder.Replace(Namespace, fileNamespace);

            filePath = filePath.Replace(".cs", ".partial.cs");
            using (var writer = new StreamWriter(filePath))
            {
                await writer.WriteAsync(clientProxyBuilder.ToString());
            }
        }

        private void GenerateMethod(
            ActionApiDescriptionModel actionApiDescription,
            MethodInfo method,
            StringBuilder clientProxyBuilder,
            List<string> usingNamespaceList)
        {
            var methodBuilder = new StringBuilder();

            var returnTypeName = GetRealTypeName(usingNamespaceList, method.ReturnType);

            if(!typeof(Task).IsAssignableFrom(method.ReturnType))
            {
                GenerateSynchronizationMethod(method, returnTypeName, methodBuilder, usingNamespaceList);
                clientProxyBuilder.Replace(MethodPlaceholder, $"{methodBuilder} {Environment.NewLine}        {MethodPlaceholder}");
                return;
            }

            GenerateAsynchronousMethod(actionApiDescription, method, returnTypeName, methodBuilder, usingNamespaceList);
            clientProxyBuilder.Replace(MethodPlaceholder, $"{methodBuilder} {Environment.NewLine}        {MethodPlaceholder}");
        }

        private void GenerateSynchronizationMethod(MethodInfo method, string returnTypeName, StringBuilder methodBuilder, List<string> usingNamespaceList)
        {
            methodBuilder.AppendLine($"public {returnTypeName} {method.Name}(<args>)");

            foreach (var parameter in method.GetParameters())
            {
                methodBuilder.Replace("<args>", $"{GetRealTypeName(usingNamespaceList, parameter.ParameterType)} {parameter.Name}, <args>");
            }

            methodBuilder.Replace("<args>", string.Empty);
            methodBuilder.Replace(", )", ")");

            methodBuilder.AppendLine("        {");
            methodBuilder.AppendLine("            //Client Proxy does not support the synchronization method, you should always use asynchronous methods as a best practice");
            methodBuilder.AppendLine("            throw new System.NotImplementedException(); ");
            methodBuilder.AppendLine("        }");
        }

        private void GenerateAsynchronousMethod(
            ActionApiDescriptionModel actionApiDescription,
            MethodInfo method,
            string returnTypeName,
            StringBuilder methodBuilder,
            List<string> usingNamespaceList)
        {
            methodBuilder.AppendLine($"public async {returnTypeName} {method.Name}(<args>)");

            foreach (var parameter in method.GetParameters())
            {
                methodBuilder.Replace("<args>", $"{GetRealTypeName(usingNamespaceList, parameter.ParameterType)} {parameter.Name}, <args>");
            }

            methodBuilder.Replace("<args>", string.Empty);
            methodBuilder.Replace(", )", ")");

            methodBuilder.AppendLine("        {");
            methodBuilder.AppendLine("            #region ActionApiDescriptionModel JSON");
            methodBuilder.AppendLine($"            var actionApiDescription = \"{JsonSerializer.Serialize(actionApiDescription).Replace("\"","\\\"")}\";");
            methodBuilder.AppendLine("            #endregion");
            methodBuilder.AppendLine("");
            methodBuilder.AppendLine("            var action = JsonSerializer.Deserialize<ActionApiDescriptionModel>(actionApiDescription);");
            methodBuilder.AppendLine("");

            if (method.ReturnType.GenericTypeArguments.IsNullOrEmpty())
            {
                methodBuilder.AppendLine("            await MakeRequestAsync(action,  <args>);");
            }
            else
            {
                methodBuilder.AppendLine($"            return await MakeRequestAsync<{returnTypeName.Replace("Task<", string.Empty)}(action, <args>);");
            }

            foreach (var parameter in method.GetParameters())
            {
                methodBuilder.Replace("<args>", $"{parameter.Name}, <args>");
            }

            methodBuilder.Replace(", <args>", string.Empty);
            methodBuilder.Replace(", )", ")");
            methodBuilder.AppendLine("");
            methodBuilder.AppendLine("        }");
        }

        private bool ShouldGenerateProxy(ControllerApiDescriptionModel controllerApiDescription)
        {
            if (!controllerApiDescription.Interfaces.Any())
            {
                return false;
            }

            var serviceInterface = controllerApiDescription.Interfaces.Last();
            return serviceInterface.Type.ToUpper().EndsWith(ServicePostfix);
        }

        private string GetRealTypeName(List<string> usingNamespaceList, Type type)
        {
            AddUsingNamespace(usingNamespaceList, type);

            if (!type.IsGenericType)
            {
                return NormalizeTypeName(type.Name);
            }

            var stringBuilder = new StringBuilder();
            stringBuilder.Append(type.Name.Substring(0, type.Name.IndexOf('`')));
            stringBuilder.Append('<');
            var appendComma = false;
            foreach (var arg in type.GetGenericArguments())
            {
                if (appendComma)
                {
                    stringBuilder.Append(',');
                }

                stringBuilder.Append(GetRealTypeName(usingNamespaceList, arg));
                appendComma = true;
            }
            stringBuilder.Append('>');
            return stringBuilder.ToString();
        }

        private void AddUsingNamespace(List<string> usingNamespaceList, Type type)
        {
            var rootNamespace = $"using {type.Namespace};";
            if (usingNamespaceList.Contains(type.Namespace) || usingNamespaceList.Any(x => rootNamespace.StartsWith(x)))
            {
                return;
            }

            usingNamespaceList.Add(rootNamespace);
        }

        private string NormalizeTypeName(string typeName)
        {
            typeName = typeName switch
            {
                "Void" => "void",
                "Boolean" => "bool",
                "String" => "string",
                "Int32" => "int",
                _ => typeName
            };

            return typeName;
        }

        private void FindAppServiceTypesRecursively(
            Type module,
            List<Type> appServiceTypes)
        {
            var types = module.Assembly
                .GetTypes()
                .Where(t => t.IsInterface)
                .Where(t => typeof(IRemoteService).IsAssignableFrom(t))
                .ToList();

            appServiceTypes.AddRange(types);

            var dependencyDescriptors = module
                .GetCustomAttributes()
                .OfType<IDependedTypesProvider>();

            foreach (var descriptor in dependencyDescriptors)
            {
                foreach (var dependedModuleType in descriptor.GetDependedTypes().Where(x=>x.Name.EndsWith("HttpApiClientModule") || x.Name.EndsWith("ApplicationContractsModule")))
                {
                    FindAppServiceTypesRecursively(dependedModuleType, appServiceTypes);
                }
            }
        }

        private static string CheckWorkDirectory(string directory)
        {
            if (!Directory.Exists(directory))
            {
                throw new CliUsageException("Specified directory does not exist.");
            }

            var projectFiles = Directory.GetFiles(directory, "*HttpApi.Client.csproj");
            if (!projectFiles.Any())
            {
                throw new CliUsageException(
                    "No project file found in the directory. The working directory must have a HttpApi.Client project file.");
            }

            return projectFiles.First();
        }

        private Type GetStartupModule(string assemblyPath)
        {
            return Assembly
                .LoadFrom(assemblyPath)
                .GetTypes()
                .SingleOrDefault(AbpModule.IsAbpModule);
        }

        private string GetTargetFrameworkVersion(string projectFilePath)
        {
            var document = new XmlDocument();
            document.Load(projectFilePath);
            return document.SelectSingleNode("//TargetFramework").InnerText;
        }
    }
}