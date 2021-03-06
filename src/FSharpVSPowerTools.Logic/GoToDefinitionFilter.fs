﻿namespace FSharpVSPowerTools.Navigation

open System
open System.IO
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio
open Microsoft.VisualStudio.TextManager.Interop
open Microsoft.VisualStudio.OLE.Interop
open Microsoft.VisualStudio.Shell.Interop
open Microsoft.VisualStudio.Shell
open Microsoft.FSharp.Compiler.SourceCodeServices
open FSharpVSPowerTools
open FSharpVSPowerTools.ProjectSystem
open FSharpVSPowerTools.CodeGeneration

type GoToDefinitionFilter(view: IWpfTextView, vsLanguageService: VSLanguageService, serviceProvider: System.IServiceProvider,
                          projectFactory: ProjectFactory) =
    let getDocumentState () =
        async {
            let dte = serviceProvider.GetService<EnvDTE.DTE, SDTE>()
            let projectItems = maybe {
                let! caretPos = view.TextBuffer.GetSnapshotPoint view.Caret.Position
                let! doc = dte.GetActiveDocument()
                let! project = projectFactory.CreateForDocument view.TextBuffer doc
                let! span, symbol = vsLanguageService.GetSymbol(caretPos, project)
                return doc.FullName, project, span, symbol }

            match projectItems with
            | Some (file, project, span, symbol) ->
                let! symbolUse = vsLanguageService.GetFSharpSymbolUse(span, symbol, file, project, AllowStaleResults.MatchingSource)
                match symbolUse with
                | Some (fsSymbolUse, fileScopedCheckResults) ->
                    let fsSymbol = fsSymbolUse.Symbol
                    let lineStr = span.Start.GetContainingLine().GetText()
                    let! findDeclResult = fileScopedCheckResults.GetDeclarationLocation(symbol.Line, symbol.RightColumn, lineStr, symbol.Text, false)
                    return Some (fsSymbol, fsSymbolUse.DisplayContext, findDeclResult) 
                | _ -> return None
            | _ -> return None
        }

    // Now the input is an entity or a member/value.
    // We always generate the full enclosing entity signature if the symbol is a member/value
    let navigateToMetadata displayContext (fsSymbol: FSharpSymbol) = 
        let fileName =
            match fsSymbol with
            | :? FSharpMemberFunctionOrValue as mem -> mem.LogicalEnclosingEntity.FullName + ".fsi"
            | _ ->
                try fsSymbol.FullName + ".fsi"
                with _ -> fsSymbol.DisplayName + ".fsi"
        let filePath = Path.Combine(Path.GetTempPath(), fileName)

        File.WriteAllText(filePath, SignatureGenerator.formatSymbol displayContext fsSymbol)
        
        let mutable hierarchy = Unchecked.defaultof<_>
        let mutable itemId = Unchecked.defaultof<_>
        let mutable windowFrame = Unchecked.defaultof<_>
        let isOpened = 
            VsShellUtilities.IsDocumentOpen(
                serviceProvider, 
                filePath, 
                Constants.LogicalViewTextGuid,
                &hierarchy,
                &itemId,
                &windowFrame)
        let canShow = 
            if isOpened then true
            else
                try
                    VsShellUtilities.OpenDocument(
                        serviceProvider, 
                        filePath, 
                        Constants.LogicalViewTextGuid, 
                        &hierarchy,
                        &itemId,
                        &windowFrame)
                    true
                with _ -> false
        if canShow then
            windowFrame.Show() |> ensureSucceeded
            let vsTextView = VsShellUtilities.GetTextView(windowFrame)
            let mutable vsTextLines = Unchecked.defaultof<_>
            vsTextView.GetBuffer(&vsTextLines) |> ensureSucceeded
            let vsTextBuffer = vsTextLines :> IVsTextBuffer
            let mutable currentFlags = 0u
            if ErrorHandler.Failed(vsTextBuffer.GetStateFlags(&currentFlags)) then ()
            else
                // Try to set buffer to read-only mode
                vsTextBuffer.SetStateFlags(currentFlags ||| uint32 BUFFERSTATEFLAGS.BSF_USER_READONLY) |> ignore

    member val IsAdded = false with get, set
    member val NextTarget: IOleCommandTarget = null with get, set

    interface IOleCommandTarget with
        member x.Exec(pguidCmdGroup: byref<Guid>, nCmdId: uint32, nCmdexecopt: uint32, pvaIn: IntPtr, pvaOut: IntPtr) =
            if pguidCmdGroup = Constants.guidOldStandardCmdSet && nCmdId = Constants.cmdidGoToDefinition then
                let statusBar = serviceProvider.GetService<IVsStatusbar, SVsStatusbar>()
                let symbolResult = getDocumentState () |> Async.RunSynchronously
                let isNamespace (fsSymbol: FSharpSymbol) =
                    match fsSymbol with
                    | :? FSharpEntity as e when e.IsNamespace -> true
                    | _ -> false

                match symbolResult with
                | Some (_, _, FindDeclResult.DeclFound _) 
                | None ->
                    // Declaration location might exist so let's Visual F# Tools handle it  
                    x.NextTarget.Exec(&pguidCmdGroup, nCmdId, nCmdexecopt, pvaIn, pvaOut)
                | Some (fsSymbol, displayContext, FindDeclResult.DeclNotFound _) ->
                    if not (isNamespace fsSymbol) then
                        navigateToMetadata displayContext fsSymbol
                        statusBar.SetText("Generated symbol metadata") |> ignore  
                    VSConstants.S_OK
            else
                x.NextTarget.Exec(&pguidCmdGroup, nCmdId, nCmdexecopt, pvaIn, pvaOut)

        member x.QueryStatus(pguidCmdGroup: byref<Guid>, cCmds: uint32, prgCmds: OLECMD[], pCmdText: IntPtr) =
            if pguidCmdGroup = Constants.guidOldStandardCmdSet && 
                prgCmds |> Seq.exists (fun x -> x.cmdID = Constants.cmdidGoToDefinition) then
                prgCmds.[0].cmdf <- (uint32 OLECMDF.OLECMDF_SUPPORTED) ||| (uint32 OLECMDF.OLECMDF_ENABLED)
                VSConstants.S_OK
            else
                x.NextTarget.QueryStatus(&pguidCmdGroup, cCmds, prgCmds, pCmdText)            