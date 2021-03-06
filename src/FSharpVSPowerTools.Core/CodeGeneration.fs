﻿namespace FSharpVSPowerTools.CodeGeneration

open System
open System.IO
open System.CodeDom.Compiler
open System.Collections.Generic
open FSharpVSPowerTools
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.SourceCodeServices

[<Measure>] type Line0
[<Measure>] type Line1

type IDocument =
    abstract FullName: string
    abstract GetText: unit -> string
    abstract GetLineText0: int<Line0> -> string
    abstract GetLineText1: int<Line1> -> string
    abstract LineCount: int 

type IRange =
    abstract StartLine: int<Line1>
    abstract StartColumn: int
    abstract EndLine: int<Line1>
    abstract EndColumn: int

type ICodeGenerationService<'Project, 'Pos, 'Range> =
    abstract TokenizeLine: 'Project * IDocument * int<Line1> -> TokenInformation list
    abstract GetSymbolAtPosition: 'Project * IDocument * pos:'Pos -> option<'Range * Symbol>
    abstract GetSymbolAndUseAtPositionOfKind: 'Project * IDocument * 'Pos * SymbolKind -> Async<option<'Range * Symbol * FSharpSymbolUse>>
    abstract ParseFileInProject: IDocument * 'Project -> Async<ParseFileResults>
    // TODO: enhance this clumsy design
    abstract ExtractFSharpPos: 'Pos -> pos


[<AutoOpen>]
module Utils =
    type internal ColumnIndentedTextWriter() =
        let stringWriter = new StringWriter()
        let indentWriter = new IndentedTextWriter(stringWriter, " ")

        member x.Write(s: string, [<ParamArray>] objs: obj []) =
            indentWriter.Write(s, objs)

        member x.WriteLine(s: string, [<ParamArray>] objs: obj []) =
            indentWriter.WriteLine(s, objs)

        member x.Indent i = 
            indentWriter.Indent <- indentWriter.Indent + i

        member x.Unindent i = 
            indentWriter.Indent <- max 0 (indentWriter.Indent - i)

        member x.Writer = 
            indentWriter :> TextWriter

        member x.Dump() =
            indentWriter.InnerWriter.ToString()

        interface IDisposable with
            member x.Dispose() =
                stringWriter.Dispose()
                indentWriter.Dispose()

    let (|IndexerArg|) = function
        | SynIndexerArg.Two(e1, e2) -> [e1; e2]
        | SynIndexerArg.One e -> [e]

    let (|IndexerArgList|) xs =
        List.collect (|IndexerArg|) xs

    let hasAttribute<'T> (attrs: seq<FSharpAttribute>) =
        attrs |> Seq.exists (fun a -> a.AttributeType.CompiledName = typeof<'T>.Name)

    let tryFindTokenLPosInRange
        (codeGenService: ICodeGenerationService<'Project, 'Pos, 'Range>) project
        (range: range) (document: IDocument) (predicate: TokenInformation -> bool) =
        // Normalize range
        // NOTE: FCS compiler sometimes returns an invalid range. In particular, the
        // range end limit can exceed the end limit of the document
        let range =
            if range.EndLine > document.LineCount then
                let newEndLine = document.LineCount
                let newEndColumn = document.GetLineText1(document.LineCount * 1<Line1>).Length
                let newEndPos = mkPos newEndLine newEndColumn
                
                mkFileIndexRange (range.FileIndex) range.Start newEndPos
            else
                range
            
        let lineIdxAndTokenSeq = seq {
            for lineIdx = range.StartLine to range.EndLine do
                yield!
                    codeGenService.TokenizeLine(project, document, (lineIdx * 1<Line1>))
                    |> List.map (fun tokenInfo -> lineIdx * 1<Line1>, tokenInfo)
        }

        lineIdxAndTokenSeq
        |> Seq.tryFind (fun (line1, tokenInfo) ->
            if range.StartLine = range.EndLine then
                tokenInfo.LeftColumn >= range.StartColumn &&
                tokenInfo.RightColumn < range.EndColumn &&
                predicate tokenInfo
            elif range.StartLine = int line1 then
                tokenInfo.LeftColumn >= range.StartColumn &&
                predicate tokenInfo
            elif int line1 = range.EndLine then
                tokenInfo.RightColumn < range.EndColumn &&
                predicate tokenInfo
            else
                predicate tokenInfo
        )
        |> Option.map (fun (line1, tokenInfo) ->
            tokenInfo, (Pos.fromZ (int line1 - 1) tokenInfo.LeftColumn)
        )