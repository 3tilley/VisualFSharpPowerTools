﻿namespace FSharpVSPowerTools.Refactoring
 
open System
open System.IO
open System.Windows
open System.Windows.Input
open System.ComponentModel
open Microsoft.FSharp.Compiler.SourceCodeServices
open FSharpVSPowerTools.ProjectSystem
open FSharpVSPowerTools

open FSharp.ViewModule
open FSharp.ViewModule.Progress
open FSharp.ViewModule.Validation

type RenameDialog = FsXaml.XAML<"RenameDialog.xaml">

// The ViewModel for our dialog
type RenameDialogViewModel(originalName: string, symbol: Symbol, initializationWorkflow : Async<(ParseAndCheckResults * SymbolDeclarationLocation * FSharpSymbol) option>, renameWorkflow : (ParseAndCheckResults -> SymbolDeclarationLocation -> string-> (OperationState -> unit) -> Async<unit>), cts : System.Threading.CancellationTokenSource) as self =
    inherit ViewModelBase()

    // This will hold the actual rename workflow arguments after the initialization async workflow completes    
    let mutable workflowArguments : (FSharpSymbol * SymbolDeclarationLocation * ParseAndCheckResults) option = None

    // Grab our synchronization context for use in async workflows as necessary
    let syncCtx = System.Threading.SynchronizationContext.Current

    // Custom validation for the name property
    let validateSymbols name =
        let check validationCheck error = if validationCheck then None else Some error

        debug "[Rename Refactoring] Check the following name: %s" name
        let name = name.Trim()
        match workflowArguments with
        | None -> Some Resource.renameErrorMessage
        | Some(fssym, _, _) ->
            match symbol.Kind, fssym with
            | _, :? FSharpUnionCase ->
                // Union cases shouldn't be lowercase
                check (isIdentifier name && not (String.IsNullOrEmpty(name) || Char.IsLower(name.[0]))) Resource.validatingUnionCase 
            | _, :? FSharpActivePatternCase ->
                    // Different from union cases, active patterns don't accept double-backtick identifiers
                    check (isIdentifier name && not (String.IsNullOrEmpty name) && Char.IsUpper(name.[0])) Resource.validatingActivePattern
            | Operator, _ -> 
                check (isOperator name) Resource.validatingOperator
            | GenericTypeParameter, _ -> 
                check (isGenericTypeParameter name) Resource.validatingGenericTypeParameter
            | StaticallyResolvedTypeParameter, _ ->
                check (isStaticallyResolvedTypeParameter name) Resource.validatingStaticallyResolvedTypeParameter
            | (Ident | Other), _ ->
                check (isIdentifier name) Resource.validatingIdentifier

    // Complete validation chain for the name property
    let validateName = 
        validate "Name" 
        >> notNullOrWhitespace 
        >> fixErrorsWithMessage Resource.validatingEmptyName
        >> custom validateSymbols
        >> result

    // Backing fields for all view-bound values
    let name = self.Factory.Backing(<@@ self.Name @@>, originalName, validateName)
    let location = self.Factory.Backing(<@@ self.Location @@>, String.Empty)
    // RenameComplete is used to close our dialog from the View automatically - should be set when we want to "complete" this operation
    let renameComplete = self.Factory.Backing(<@@ self.RenameComplete @@>, false)
    // Initialized allows us to track when the initialization async workflow completes
    let initialized = self.Factory.Backing(<@@ self.Initialized @@>, false)    
    // Create a progress manager to track the progress status
    let progress = ProgressManager()

    // This provides us a simple way to report progress later
    let report = Some(updateProgress progress)

    // We wrap up the actual async workflow in order to close us when renameComplete as well as mark us executing while we run
    let wrappedWorkflow _ newName =
        async {            
            reportProgress report (Reporting("Performing Rename..."))
            // If the user just accepts with the original name, just make this a no-op instead of an error
            if not(newName = originalName) then
                match workflowArguments with
                | Some(_, location, results) ->
                    do! renameWorkflow results location newName report.Value
                | _ -> ()
            renameComplete.Value <- true
        }

    // The async command which is executed when the user clicks OK
    let execute = self.Factory.CommandAsyncParamChecked(wrappedWorkflow, (fun _ -> self.IsValid && self.Initialized), [ <@@ self.IsValid @@> ; <@@ self.Initialized @@> ], cts.Token)
    
    // Cancelling the operation should cancel all async workflows and close us    
    let cancelCommand = self.Factory.CommandSync(fun _ -> cts.Cancel())

    do  
        // Make us close if we're canceled
        cts.Token.Register(fun _ -> renameComplete.Value <- true) |> ignore

        // Force initialization to re-evaluate Name's validation by making it a dependency      
        self.DependencyTracker.AddPropertyDependency(<@@ self.Name @@>, <@@ self.Initialized @@>)
        
        // Perform our initialization routine while reporting progress/etc as necessary
        Async.StartImmediateSafe(
            async {                 
                reportProgress report (Reporting("Initializing..."))
                let! b = initializationWorkflow 
                match b with
                | Some(results, location, symbol) ->
                    do! Async.SwitchToContext syncCtx
                    workflowArguments <- Some(symbol, location, results)                    
                    reportProgress report Idle
                    self.Location <- 
                        let fullName = symbol.FullName
                        let displayName = symbol.DisplayName
                        if fullName.EndsWith displayName then
                            let locationLength = max 0 (fullName.Length - (displayName.Length + 1))
                            fullName.Remove locationLength
                        else fullName
                    initialized.Value <- true                    
                | None -> 
                    cts.Cancel()                    
            }, cts.Token)

    // Our bound properties
    member x.Name with get() = name.Value and set(v) = name.Value <- v
    member x.Location with get() = location.Value and set(v) = location.Value <- v

    // Related to progress / status reporting
    member x.Progress = progress

    // Handles lifecycle tracking (when things should be enabled, closed, etc)
    member x.Initialized = initialized.Value    
    member x.RenameComplete = renameComplete.Value

    // The actual commands for the buttons
    member x.ExecuteCommand = execute               
    member x.CancelCommand = cancelCommand
    

// This handles "code behind", ie: pure view logic for our dialog
type RenameDialogViewController() =
    interface FsXaml.IViewController with
        member this.Attach fe =
            // Use the TypeProvider's Accessor sub-type to gain access to named members
            let window = RenameDialog.Accessor fe

            let model = window.Root.DataContext :?> INotifyPropertyChanged
            // Once the model is initialized, focus and select txtName so the user can just type "F2 / new_name / Enter"
            model.PropertyChanged.Add(fun e ->
                if e.PropertyName = "Initialized" then
                    window.Root.Activate() |> ignore
                    window.txtName.IsEnabled <- true
                    window.txtName.SelectAll()
                    window.txtName.Focus() |> ignore)

// Module for loading the rename dialog with a viewModel + owner
[<RequireQualifiedAccess>]
module UI =
    let loadRenameDialog (viewModel: RenameDialogViewModel) owner =
        let window = RenameDialog().CreateRoot()
        window.Owner <- owner
        window.DataContext <- viewModel        
        window

