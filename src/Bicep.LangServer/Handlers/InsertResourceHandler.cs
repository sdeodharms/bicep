// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Deployments.Core.Comparers;
using Azure.Deployments.Core.Definitions.Identifiers;
using Azure.Identity;
using Azure.ResourceManager;
using Bicep.Core;
using Bicep.Core.CodeAction;
using Bicep.Core.Diagnostics;
using Bicep.Core.Emit;
using Bicep.Core.Parsing;
using Bicep.Core.Resources;
using Bicep.Core.Semantics;
using Bicep.Core.Syntax;
using Bicep.Core.Tracing;
using Bicep.Core.TypeSystem.Az;
using Bicep.LanguageServer.CompilationManager;
using Bicep.LanguageServer.Extensions;
using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Bicep.Core.PrettyPrint;
using Bicep.Core.PrettyPrint.Options;
using Azure.Core.Serialization;
using Azure.ResourceManager.Resources;
using Bicep.Core.Workspaces;
using Bicep.LanguageServer.Utils;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Bicep.Core.Navigation;
using Bicep.Core.Rewriters;
using System.Text.RegularExpressions;

namespace Bicep.LanguageServer.Handlers
{
    [Method("textDocument/insertResource", Direction.ClientToServer)]
    public record InsertResourceParams : TextDocumentPositionParams, IRequest
    {
        public string? ResourceId { get; init; }

        public bool Recurse { get; init; }
    }

    public class InsertResourceHandler : IJsonRpcNotificationHandler<InsertResourceParams>
    {
        private readonly ILanguageServerFacade server;
        private readonly ICompilationManager compilationManager;
        private readonly IAzResourceTypeLoader azResourceTypeLoader;

        public InsertResourceHandler(ILanguageServerFacade server, ICompilationManager compilationManager, IAzResourceTypeLoader azResourceTypeLoader)
        {
            this.server = server;
            this.compilationManager = compilationManager;
            this.azResourceTypeLoader = azResourceTypeLoader;
        }

        public async Task<Unit> Handle(InsertResourceParams request, CancellationToken cancellationToken)
        {
            try
            {
                var context = compilationManager.GetCompilation(request.TextDocument.Uri.ToUri());
                if (context is null)
                {
                    return Unit.Value;
                }

                if (!ResourceId.TryParse(request.ResourceId, out var resourceId))
                {
                    return Unit.Value;
                }
                var fullyQualifiedType = resourceId.FormatFullyQualifiedType();

                var allTypes = azResourceTypeLoader.GetAvailableTypes()
                    .ToLookup(x => x.FullyQualifiedType, StringComparer.OrdinalIgnoreCase);

                var matchedType = allTypes[fullyQualifiedType]
                    .OrderByDescending(x => x.ApiVersion, ApiVersionComparer.Instance)
                    .FirstOrDefault();

                if (matchedType is null)
                {
                    return Unit.Value;
                }

                var options = new ArmClientOptions();
                options.Diagnostics.ApplySharedResourceManagerSettings();

                var armClient = new ArmClient(new AzureCliCredential(), options);

                var response = await armClient.GetGenericResource(resourceId.FullyQualifiedId).GetAsync(cancellationToken);
                if (response is null ||
                    response.GetRawResponse().ContentStream is not { } contentStream)
                {
                    return Unit.Value;
                }

                contentStream.Position = 0;
                var resource = await JsonSerializer.DeserializeAsync<JsonElement>(contentStream);

                // TODO replace name with fully-qualified name before generating syntax.
                var resourceDeclaration = CreateResourceSyntax(resource, resourceId, matchedType);

                var offset = PositionHelper.GetOffset(context.LineStarts, request.Position);
                var replacement = Process(context.Compilation, resourceDeclaration, new TextSpan(offset, 0));

                await server.Workspace.ApplyWorkspaceEdit(new ApplyWorkspaceEditParams
                {
                    Edit = new()
                    {
                        Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                        {
                            [request.TextDocument.Uri] = new[] {
                            new TextEdit
                            {
                                Range = replacement.ToRange(context.LineStarts),
                                NewText = replacement.Text,
                            },
                        },
                        },
                    },
                }, cancellationToken);

                return Unit.Value;
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"{nameof(InsertResourceHandler)}.{nameof(Handle)}: {exception}");
                throw;
            }
        }

        private CodeReplacement Process(Compilation prevCompilation, ResourceDeclarationSyntax resourceDeclaration, TextSpan replacementSpan)
        {
            var program = new ProgramSyntax(
                new[] { resourceDeclaration },
                SyntaxFactory.CreateToken(TokenType.EndOfFile),
                ImmutableArray<IDiagnostic>.Empty);

            var printed = PrettyPrinter.PrintProgram(program, new PrettyPrintOptions(NewlineOption.LF, IndentKindOption.Space, 2, false));

            var bicepFile = SourceFileFactory.CreateBicepFile(new Uri("inmemory:///generated.bicep"), printed);

            for (var i = 0; i < 5; i++)
            {
                var model = new SemanticModel(prevCompilation, bicepFile, prevCompilation.SourceFileGrouping.FileResolver, prevCompilation.Configuration);
                var updated = new TypeCasingFixerRewriter(model).Rewrite(bicepFile.ProgramSyntax);
                bicepFile = SourceFileFactory.CreateBicepFile(bicepFile.FileUri, updated.ToTextPreserveFormatting());

                model = new SemanticModel(prevCompilation, bicepFile, prevCompilation.SourceFileGrouping.FileResolver, prevCompilation.Configuration);
                updated = new ReadOnlyPropertyRemovalRewriter(model).Rewrite(bicepFile.ProgramSyntax);
                bicepFile = SourceFileFactory.CreateBicepFile(bicepFile.FileUri, updated.ToTextPreserveFormatting());
            }

            printed = PrettyPrinter.PrintProgram(bicepFile.ProgramSyntax, new PrettyPrintOptions(NewlineOption.LF, IndentKindOption.Space, 2, false));
            return new CodeReplacement(replacementSpan, printed);
        }

        private static ResourceDeclarationSyntax CreateResourceSyntax(JsonElement resource, ResourceId resourceId, ResourceTypeReference typeReference)
        {
            return new ResourceDeclarationSyntax(
                ImmutableArray<SyntaxBase>.Empty,
                SyntaxFactory.CreateToken(TokenType.Identifier, "resource"),
                SyntaxFactory.CreateIdentifier(Regex.Replace(resourceId.NameHierarchy.Last(), "[^a-zA-Z]", "")),
                SyntaxFactory.CreateStringLiteral(typeReference.FormatName()),
                null,
                SyntaxFactory.CreateToken(TokenType.Assignment),
                ProcessElement(resource));
        }

        private static SyntaxBase ProcessElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var properties = new List<ObjectPropertySyntax>();
                    foreach (var property in element.EnumerateObject())
                    {
                        properties.Add(SyntaxFactory.CreateObjectProperty(property.Name, ProcessElement(property.Value)));
                    }
                    return SyntaxFactory.CreateObject(properties);
                case JsonValueKind.Array:
                    var items = new List<SyntaxBase>();
                    foreach (var value in element.EnumerateArray())
                    {
                        items.Add(ProcessElement(value));
                    }
                    return SyntaxFactory.CreateArray(items);
                case JsonValueKind.String:
                    return SyntaxFactory.CreateStringLiteral(element.GetString()!);
                case JsonValueKind.Number:
                    if (element.TryGetInt32(out var intValue))
                    {
                        return SyntaxFactory.CreateIntegerLiteral(element.GetInt32()!);
                    }
                    return SyntaxFactory.CreateStringLiteral(element.ToString()!);
                case JsonValueKind.True:
                    return SyntaxFactory.CreateToken(TokenType.TrueKeyword);
                case JsonValueKind.False:
                    return SyntaxFactory.CreateToken(TokenType.FalseKeyword);
                case JsonValueKind.Null:
                    return SyntaxFactory.CreateToken(TokenType.NullKeyword);
                default:
                    throw new InvalidOperationException($"Failed to deserialize JSON");
            }
        }
    }
}