﻿// $begin{copyright}
//
// This file is part of WebSharper
//
// Copyright (c) 2008-2016 IntelliFactory
//
// Licensed under the Apache License, Version 2.0 (the "License"); you
// may not use this file except in compliance with the License.  You may
// obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
// implied.  See the License for the specific language governing
// permissions and limitations under the License.
//
// $end{copyright}

// Main translation module from .NET forms to JavaScript
module WebSharper.Compiler.Translator
 
open WebSharper.Core
open WebSharper.Core.AST
open WebSharper.Compiler

module M = WebSharper.Core.Metadata
module I = WebSharper.Core.AST.IgnoreSourcePos

/// Debug-only checker for invalid forms after transformation to have localized error.
/// Otherwise error is thrown when writing to JavaScript after packaging.
type CheckNoInvalidJSForms(comp: Compilation, isInline) as this =
    inherit TransformerWithSourcePos(comp)

    let invalidForm f = 
        this.Error("Invalid form after JS tranlation: " + f)

    override this.TransformSelf () = invalidForm "Self"
    override this.TransformBase () = invalidForm "Base"
    override this.TransformHole a = if isInline then base.TransformHole(a) else invalidForm "Hole"
    override this.TransformFieldGet (_,_,_) = invalidForm "FieldGet"
    override this.TransformFieldSet (_,_,_,_) = invalidForm "FieldSet"
    override this.TransformLet (a, b, c) = if isInline then base.TransformLet(a, b, c) else invalidForm "Let" 
    override this.TransformLetRec (_,_) = invalidForm "LetRec"
    override this.TransformStatementExpr (a, b) = if isInline then base.TransformStatementExpr(a, b) else invalidForm "StatementExpr"
    override this.TransformAwait _  = invalidForm "Await"
    override this.TransformNamedParameter (_,_) = invalidForm "NamedParameter"
    override this.TransformRefOrOutParameter _ = invalidForm "RefOrOutParamete"
    override this.TransformCtor (a, b, c) = if isInline then base.TransformCtor(a, b, c) else invalidForm "Ctor"
    override this.TransformCoalesce (_,_,_) = invalidForm "Coalesce"
    override this.TransformTypeCheck (_,_) = invalidForm "TypeCheck"
    override this.TransformCall (a, b, c, d) = if isInline then base.TransformCall(a, b, c, d) else invalidForm "Call"

type RemoveLets() =
    inherit Transformer()
    
    override this.TransformExpression (a) =
        base.TransformExpression(removeLets a)
                                
let removeLetsTr = RemoveLets()

type RuntimeCleaner(forced) =
    inherit Transformer()
    
    override this.TransformExpression (a) =
        base.TransformExpression(Optimizations.cleanRuntime forced a)

let private runtimeCleaner = RuntimeCleaner(false)
let private runtimeCleanerForced = RuntimeCleaner(true)

type Breaker(isInline) =
    inherit Transformer()

    override this.TransformStatement (a) =
#if DEBUG
        if logTransformations then
            printfn "breaker start: %s" (Debug.PrintStatement a)
#endif
        let opt = a |> optimizer.TransformStatement
        let res = if isInline then opt else BreakStatement opt
#if DEBUG
        if logTransformations then
            printfn "breaker result: %s" (Debug.PrintStatement res)
#endif
        res
let private breaker = Breaker(false)
let private inlineOptimizer = Breaker(true)

type CollectCurried() =
    inherit Transformer()

    override this.TransformFunction(args, body) =
        match Function(args, body) with
        | CurriedFunction(a, b) ->
            let trFunc, moreArgs, n =
                match b with
                | I.Return (I.Application (f, ar, _, Some _)) ->
                    let moreArgsLength = ar.Length - a.Length
                    if moreArgsLength >= 0 then
                        let moreArgs, lastArgs = ar |> List.splitAt moreArgsLength
                        if sameVars a lastArgs && VarsNotUsed(args).Get(Sequential moreArgs) then
                            this.TransformExpression f, moreArgs, ar.Length
                        else base.TransformFunction(a, b), [], a.Length
                    else base.TransformFunction(a, b), [], a.Length
                | _ -> base.TransformFunction(a, b), [], a.Length
            if n = 2 then
                base.TransformFunction(args, body)    
            elif n < 4 || moreArgs.Length = 0 then
                let curr =
                    match n with
                    | 2 -> JSRuntime.Curried2 trFunc 
                    | 3 -> JSRuntime.Curried3 trFunc 
                    | _ -> JSRuntime.Curried trFunc n
                List.fold (fun f x -> Application(f, [this.TransformExpression x], NonPure, Some 1)) curr moreArgs
            else
                JSRuntime.CurriedA trFunc (n - moreArgs.Length) (NewArray moreArgs)
                
        | SimpleFunction f ->
            f
        | _ -> base.TransformFunction(args, body)   
   
let collectCurriedTr = CollectCurried() 

let collectCurried isCtor body =
    // do not optimize away top function if it is a constructor
    // function identity is important for Runtime.Ctor
    if isCtor then
        match body with
        | Function(args, cbody) ->
            Function (args, collectCurriedTr.TransformStatement cbody)
        | _ ->
            collectCurriedTr.TransformExpression body
    else   
        collectCurriedTr.TransformExpression body

let defaultRemotingProvider =
    TypeDefinition {
        Assembly = "WebSharper.Main"
        FullName =  "WebSharper.Remoting+AjaxRemotingProvider"
    }, []
    
let emptyConstructor = Hashed { CtorParameters = [] }

let private getItem n x = ItemGet(x, Value (String n), Pure)
let private getIndex n x = ItemGet(x, Value (Int n), Pure)

let private getItemRO n isReadonly x =
    if isReadonly then
        getItem n x
    else
        ItemGet(x, Value (String n), NoSideEffect)

let private getIndexRO n isReadonly x =
    if isReadonly then
        getIndex n x
    else
        ItemGet(x, Value (Int n), NoSideEffect)

type GenericInlineResolver (generics) =
    inherit Transformer()

    let gs = Array.ofSeq generics 

    let subs (t: Type) = t.SubstituteGenerics(gs)

    override this.TransformCall (thisObj, typ, meth, args) =
        Call (
            thisObj |> Option.map this.TransformExpression, 
            Generic typ.Entity (typ.Generics |> List.map subs),
            Generic meth.Entity (meth.Generics |> List.map subs), 
            args |> List.map this.TransformExpression
        )
        
    override this.TransformCtor(typ, ctor, args) =
        Ctor (
            Generic typ.Entity (typ.Generics |> List.map subs),
            ctor, 
            args |> List.map this.TransformExpression
        )

    override this.TransformTraitCall(typs, meth, args) =
        TraitCall (
            typs |> List.map subs,
            Generic meth.Entity (meth.Generics |> List.map subs), 
            args |> List.map this.TransformExpression
        )

    override this.TransformTypeCheck(expr, typ) =
        TypeCheck (
            expr |> this.TransformExpression,
            typ |> subs
        )

let private objTy = NonGenericType Definitions.Obj

let rpcMethodNode name ret =
    M.AbstractMethodNode (Definitions.IRemotingProvider, Method {
        MethodName = name
        Parameters = [ NonGenericType Definitions.String; ArrayType (objTy, 1) ]
        ReturnType = ret
        Generics = 0       
    })

let private syncRpcMethodNode = rpcMethodNode "Sync" objTy
let private asyncRpcMethodNode = rpcMethodNode "Async" (GenericType Definitions.Async [objTy])
let private taskRpcMethodNode = rpcMethodNode "Task" (GenericType Definitions.Task1 [objTy])
let private sendRpcMethodNode = rpcMethodNode "Send" VoidType

type TypeCheckKind =
    | TypeOf of string
    | InstanceOf of Address
    | IsNull
    | PlainObject
    | OtherTypeCheck

let tryGetTypeCheck kind expr =
    match kind with
    | TypeOf t ->
        Binary (
            Unary(UnaryOperator.typeof, expr),
            BinaryOperator.``==``,
            Value (String t)
        ) |> Some
    | InstanceOf a ->
        Binary(expr, BinaryOperator.instanceof, GlobalAccess a) |> Some
    | IsNull ->
        Binary(expr, BinaryOperator.``===``, Value Null) |> Some
    | _ ->
        None

type DotNetToJavaScript private (comp: Compilation, ?inProgress) =
    inherit TransformerWithSourcePos(comp)

    let inProgress = defaultArg inProgress []
    let mutable selfAddress = None
    let mutable currentNode = M.AssemblyNode ("", false) // placeholder
    let mutable currentIsInline = false
    let mutable hasDelayedTransform = false
    let mutable currentFuncArgs = None

    let removeSourcePosFromInlines expr =
        if currentIsInline then removeSourcePos.TransformExpression expr else expr

    let modifyDelayedInlineInfo (info: M.CompiledMember) =
        if hasDelayedTransform then 
            let rec m info =
                match info with 
                | M.Macro (t, p, fb) -> M.Macro(t, p, fb |> Option.map m)
                | _ -> M.NotCompiledInline 
            m info
        else info

    let isInline info =
        let rec ii m =
            match m with 
            | M.Inline -> true
            | M.Macro(_, _, Some f) -> ii f
            | _ -> false
        match info with        
        | NotCompiled (m, _, _) 
        | NotGenerated (_, _, m, _, _) -> ii m

    let breakExpr e = 
        if currentIsInline then
            e 
            |> removeLetsTr.TransformExpression
            |> inlineOptimizer.TransformExpression
        else
            let isCtor =
                match currentNode with
                | M.ConstructorNode _ -> true
                | _ -> false 
            e 
            |> removeLetsTr.TransformExpression
            |> runtimeCleaner.TransformExpression
            |> breaker.TransformExpression
            |> runtimeCleanerForced.TransformExpression
            |> collectCurried isCtor

    member this.CheckResult (res) =
#if DEBUG
        if hasDelayedTransform then res else
            CheckNoInvalidJSForms(comp, currentIsInline).TransformExpression res
#else
        res
#endif
     
    member this.Generate(g, p, m) =
        match comp.GetGeneratorInstance(g) with
        | Some gen ->
            let genResult = 
                try
                    gen.Generate {
                        Member = m
                        Parameter = p
                        Compilation = comp
                    }
                with e -> GeneratorError e.Message
            let verifyFunction gres =
                match IgnoreExprSourcePos gres with 
                | Function _
                | FuncWithThis _ -> gres
                | _ -> this.Error(sprintf "Generator not returning a function: %s" g.Value.FullName)
            let rec getExpr gres = 
                match gres with
                | GeneratedQuotation q -> 
                    QuotationReader.transformExpression (QuotationReader.Environment.New(comp)) q
                    |> verifyFunction |> this.TransformExpression |> breakExpr
                | GeneratedAST resExpr -> resExpr |> verifyFunction |> this.TransformExpression |> breakExpr
                | GeneratedString s -> Recognize.parseGeneratedString s
                | GeneratedJavaScript js -> Recognize.parseGeneratedJavaScript js
                | GeneratorError msg ->
                    this.Error(sprintf "Generator error in %s: %s" g.Value.FullName msg)
                | GeneratorWarning (msg, gres) ->
                    this.Warning (sprintf "Generator warning in %s: %s" g.Value.FullName msg)
                    getExpr gres
            getExpr genResult
        | None ->
            if comp.UseLocalMacros then
                this.Error("Getting generator failed")
            else
                this.Warning("Could not run generator in code service.")
                Undefined       

    member this.CustomTypeConstructor (typ : Concrete<TypeDefinition>, i : M.CustomTypeInfo, ctor: Constructor, args) =
        match i with
        | M.FSharpRecordInfo fields ->
            this.TransformNewRecord(typ, args)
        | _ -> this.Error("Unhandled F# compiler generated constructor")
    
    member this.CustomTypeMethod (objExpr : option<Expression>, typ : Concrete<TypeDefinition>, i : M.CustomTypeInfo, meth: Concrete<Method>, args) =
        let me = meth.Entity.Value
        let unionCase isSingleCase (c: M.FSharpUnionCaseInfo) =
            let mN = me.MethodName
            if mN.StartsWith "get_" then
                let fN = mN.[4 ..]
                let getUnionBaseType td =
                    if isSingleCase then td else
                    let n = td.FullName
                    { td with FullName = n.Substring(0, n.LastIndexOf('+')) }
                let uTyp = 
                    { typ with
                        Entity = TypeDefinition (getUnionBaseType typ.Entity.Value)
                    }
                this.TransformUnionCaseGet(objExpr.Value, uTyp, c.Name, fN)
                |> Some
            else 
                None

        match i with
        | M.DelegateInfo _ ->
            match me.MethodName with
            | "Invoke" ->
                // TODO: optional arguments
                Application(this.TransformExpression objExpr.Value, args |> List.map this.TransformExpression, NonPure, Some args.Length)
            | "op_Addition" -> JSRuntime.CombineDelegates (NewArray (args |> List.map this.TransformExpression))
            | "op_Equality" -> 
                match args |> List.map this.TransformExpression with
                | [ d1; d2 ] ->
                    JSRuntime.DelegateEqual d1 d2
                | _ -> this.Error("Delegate equality check expects two arguments")
            | "ToString" -> Value (String typ.Entity.Value.FullName)
            | mn -> this.Error("Unrecognized delegate method: " + mn)
        | M.FSharpRecordInfo _ ->
            match me.MethodName.[.. 2] with
            | "get" ->
                let fn = me.MethodName.[4 ..]
                this.TransformFieldGet(objExpr, typ, fn)
            | "set" -> 
                let fn = me.MethodName.[4 ..]
                this.TransformFieldSet(objExpr, typ, fn, args.Head)
            | _ -> 
                match me.MethodName with
                | "ToString" -> Value (String typ.Entity.Value.FullName)
                | _ -> this.Error("Unrecognized member of F# record type")         
        | M.FSharpUnionInfo u ->
            // union types with a single non-null case do not have
            // nested subclass subclass for the case
            let checkSingleCaseUnion =
                let numCases = u.Cases.Length
                if numCases = 1 then
                    Some u.Cases.Head
                elif (u.HasNull && numCases = 2) then
                    if u.Cases.Head.Kind = M.ConstantFSharpUnionCase Null then
                        Some u.Cases.Tail.Head
                    else
                        Some u.Cases.Head     
                else None 
                |> Option.bind (unionCase true)
            match checkSingleCaseUnion with
            | Some res -> res
            | _ ->
            let mN = me.MethodName
            let styp() =
                // substituted generic arguments are needed for erased choice
                let mgen = Array.ofList meth.Generics
                { typ with
                    Generics = typ.Generics |> List.map (fun t -> t.SubstituteGenerics(mgen))
                }
            if mN.StartsWith "get_Is" then
                let cN = mN.[6 ..]
                let u =
                    match objExpr with
                    | Some u -> u
                    | _ -> args.Head
                this.TransformUnionCaseTest(u, styp(), cN)
            elif mN = "get_Tag" then
                let u =
                    match objExpr with
                    | Some u -> u
                    | _ -> args.Head
                this.TransformUnionCaseTag(u, styp())
            elif mN.StartsWith "New" then 
                let cN = mN.[3 ..]
                this.TransformNewUnionCase(typ, cN, args)
            elif mN.StartsWith "get_" then
                if erasedUnions.Contains typ.Entity then
                    if mN = "get_Undefined" then Undefined else
                    this.TransformExpression objExpr.Value
                else
                let cN = mN.[4 ..]
                let i, c = 
                    try
                        u.Cases |> Seq.indexed |> Seq.find (fun (_, c) -> c.Name = cN)
                    with _ ->
                        failwithf "Failed to find union case %s in %s, found: %s" cN typ.Entity.Value.FullName (u.Cases |> Seq.map (fun c -> c.Name) |> String.concat ", ")

                match c.Kind with
                | M.ConstantFSharpUnionCase v -> Value v
                | M.SingletonFSharpUnionCase -> 
                    this.TransformCopyCtor(typ.Entity, Object [ "$", Value (Int i) ])
                | M.NormalFSharpUnionCase _ -> 
                    failwith "A union case with a property getter should not have fields"
            else
                match mN with
                | "ToString" -> Value (String typ.Entity.Value.FullName)
                | _ -> this.Error("Unrecognized F# compiler generated method for union: " + mN)                 
        | M.FSharpUnionCaseInfo c -> 
            match unionCase false c with
            | Some res -> res
            | _ -> this.Error("Unrecognized F# compiler generated method for union case: " + me.MethodName)    
        | _ -> this.Error("Unrecognized F# compiler generated method: " + me.MethodName)
     
    member this.CompileMethod(info, expr, typ, meth) =
        currentNode <- M.MethodNode(typ, meth) 
#if DEBUG
        if meth.Value.MethodName.StartsWith "DebugCompiler" then
            printfn "Logging transformations: %s" meth.Value.MethodName
            logTransformations <- true
            printfn "Translator start: %s" (Debug.PrintExpression expr)
#endif      
        if inProgress |> List.contains currentNode then
            let msg = sprintf "Inline loop found at method %s.%s" typ.Value.FullName meth.Value.MethodName
            comp.AddError(None, SourceError msg)
            comp.FailedCompiledMethod(typ, meth)
        else
        // for C# static auto-properties
        selfAddress <- 
            comp.TryLookupClassInfo(typ) |> Option.bind (fun cls ->
                cls.StaticConstructor |> Option.map (fun (a, _) -> Address (List.tail a.Value))    
            )
        currentIsInline <- isInline info
        match info with
        | NotCompiled (i, notVirtual, opts) ->
            currentFuncArgs <- opts.FuncArgs
            let res = this.TransformExpression expr |> removeSourcePosFromInlines |> breakExpr
            let res = this.CheckResult(res)
            let opts =
                { opts with
                    IsPure = notVirtual && (opts.IsPure || isPureFunction res)
                } 
            comp.AddCompiledMethod(typ, meth, modifyDelayedInlineInfo i, opts, res)
        | NotGenerated (g, p, i, notVirtual, opts) ->
            let m = GeneratedMethod(typ, meth)
            let res = this.Generate (g, p, m) |> breakExpr
            let res = this.CheckResult(res)
            let opts =
                { opts with
                    IsPure = notVirtual && (opts.IsPure || isPureFunction res)
                }
            comp.AddCompiledMethod(typ, meth, modifyDelayedInlineInfo i, opts, res)
#if DEBUG
        logTransformations <- false
#endif

    member this.CompileImplementation(info, expr, typ, intf, meth) =
        currentNode <- M.ImplementationNode(typ, intf, meth)
        currentIsInline <- isInline info // TODO: implementations should not be inlined
        match info with
        | NotCompiled (i, _, _) -> 
            let res = this.TransformExpression expr |> breakExpr
            let res = this.CheckResult(res)
            comp.AddCompiledImplementation(typ, intf, meth, i, res)
        | NotGenerated (g, p, i, _, _) ->
            let m = GeneratedImplementation(typ, intf, meth)
            let res = this.Generate (g, p, m) |> breakExpr
            let res = this.CheckResult(res)
            comp.AddCompiledImplementation(typ, intf, meth, i, res)

    member this.CompileConstructor(info, expr, typ, ctor) =
        currentNode <- M.ConstructorNode(typ, ctor)
        if inProgress |> List.contains currentNode then
            let msg = sprintf "inline loop found at constructor of %s" typ.Value.FullName
            comp.AddError(None, SourceError msg)
            comp.FailedCompiledConstructor(typ, ctor)
        else
        currentIsInline <- isInline info
        match info with
        | NotCompiled (i, _, opts) -> 
            currentFuncArgs <- opts.FuncArgs
            let res = this.TransformExpression expr |> removeSourcePosFromInlines |> breakExpr
            let res = this.CheckResult(res)
            let opts =
                { opts with
                    IsPure = opts.IsPure || isPureFunction res
                }
            comp.AddCompiledConstructor(typ, ctor, modifyDelayedInlineInfo i, opts, res)
        | NotGenerated (g, p, i, _, opts) ->
            let m = GeneratedConstructor(typ, ctor)
            let res = this.Generate (g, p, m) |> breakExpr
            let res = this.CheckResult(res)
            let opts =
                { opts with
                    IsPure = opts.IsPure || isPureFunction res
                }
            comp.AddCompiledConstructor(typ, ctor, modifyDelayedInlineInfo i, opts, res)

    member this.CompileStaticConstructor(addr, expr, typ) =
        currentNode <- M.TypeNode typ
        selfAddress <- 
            let cls = comp.TryLookupClassInfo(typ).Value
            let addr = fst cls.StaticConstructor.Value 
            Some (Address (List.tail addr.Value))
        let res = this.TransformExpression expr |> breakExpr
        let res = this.CheckResult(res)
        comp.AddCompiledStaticConstructor(typ, addr, res)

    static member CompileFull(comp: Compilation) =
        for t, c, i, e in comp.GetCompilingConstructors() do
            let toJS = DotNetToJavaScript(comp)
            toJS.CompileConstructor(i, e, t, c)

        for t, a, e in comp.GetCompilingStaticConstructors() do
            let toJS = DotNetToJavaScript(comp)
            toJS.CompileStaticConstructor(a, e, t)

        for t, it, m, i, e in comp.GetCompilingImplementations() do
            let toJS = DotNetToJavaScript(comp)
            toJS.CompileImplementation(i, e, t, it, m)

        match comp.EntryPoint with
        | Some ep ->
            let toJS = DotNetToJavaScript(comp)
            comp.EntryPoint <- Some (toJS.TransformStatement(ep))
        | _ -> ()

        let compileMethods() =
            while comp.CompilingMethods.Count > 0 do
                let toJS = DotNetToJavaScript(comp)
                let (KeyValue((t, m), (i, e))) =  Seq.head comp.CompilingMethods
                toJS.CompileMethod(i, e, t, m)

        compileMethods()
        comp.CloseMacros()
        compileMethods()

    static member CompileExpression (comp, expr) =
        DotNetToJavaScript(comp).TransformExpression(expr)

    member this.AnotherNode() = DotNetToJavaScript(comp, currentNode :: inProgress)    

    member this.AddDependency(dep: M.Node) =
        comp.Graph.AddEdge(currentNode, dep)

    member this.AddTypeDependency(typ) =
        let typ = comp.FindProxied typ
        if comp.HasType typ then
            comp.Graph.AddEdge(currentNode, M.TypeNode typ)

    member this.AddConstructorDependency(typ, ctor) =
        let typ = comp.FindProxied typ
        if comp.ConstructorExistsInMetadata (typ, ctor) then
            comp.Graph.AddEdge(currentNode, M.ConstructorNode (typ, ctor))
        else
            this.AddTypeDependency(typ)

    member this.AddMethodDependency(typ, meth) =
        let typ = comp.FindProxied typ
        if comp.MethodExistsInMetadata (typ, meth) then
            if comp.IsInterface typ then
                comp.Graph.AddEdge(currentNode, M.AbstractMethodNode (typ, meth))
            else
                comp.Graph.AddEdge(currentNode, M.MethodNode (typ, meth))
        else
            if comp.HasType typ then
                comp.Graph.AddEdge(currentNode, M.TypeNode typ)

    member this.Error(err) =
        comp.AddError(this.CurrentSourcePos, err)
        errorPlaceholder

    member this.OptimizeArg (opt, expr) =
        match opt with
        | NotOptimizedFuncArg -> expr
        | CurriedFuncArg currying ->
            match IgnoreExprSourcePos expr with 
            | OptimizedFSharpArg(f, CurriedFuncArg arity) when arity = currying ->
                f
            | _ ->
                let cargs = List.init currying (fun _ -> Id.New(mut = false))
                Lambda(cargs, CurriedApplication(expr, cargs |> List.map Var))  
        | TupledFuncArg tupling -> 
            match expr with
            | TupledLambda (args, body, _) ->
                Lambda(List.ofSeq args, body)
            | _ ->
                match IgnoreExprSourcePos expr with
                | OptimizedFSharpArg(f, TupledFuncArg arity) when arity = tupling -> 
                    f
                | _ ->
                    let args = List.init tupling (fun _ -> Id.New(mut = false))
                    Lambda(args, Application(expr, [NewArray(args |> List.map Var)], NonPure, Some 1))

    override this.TransformOptimizedFSharpArg(f, opt) =
        match opt with
        | CurriedFuncArg arity ->
            let rec c args a =
                if a = 0 then
                    Application (f, List.rev args, NonPure, Some arity)
                else
                    let x = Id.New(mut = false)
                    Lambda ([x], c (Var x :: args) (a - 1))
            c [] arity
        | TupledFuncArg arity ->
            let x = Id.New(mut = false)
            let args =
                List.init arity (fun i -> (Var x).[Value (Int i)])
            Lambda ([x], Application (f, args, NonPure, Some arity))
        | _ ->
            this.TransformExpression(f)

    member this.HandleMacroNeedsResolvedTypeArg(t, macroName) =
        match t with
        | TypeParameter i 
        | StaticTypeParameter i ->
            this.Error(sprintf "Macro '%s' requires a resolved type argument for type parameter index %d. Mark the member with the Inline attribute." macroName i)
        | LocalTypeParameter ->
            this.Error(sprintf "Macro '%s' would use a local type parameter. Make the inner function non-generic or move it to module level and mark it with the Inline attribute" macroName)
        | _ -> 
            this.Error(sprintf "Macro '%s' erroneusly reported MacroNeedsResolvedTypeArg on not a type parameter." macroName)

    member this.CompileCall (info, opts: M.Optimizations, expr, thisObj, typ, meth, args, ?baseCall) =
        let opts =
            match opts.Warn with
            | Some w -> 
                this.Warning(w)
                { opts with Warn = None } // do not generate warning again on recursive calls
            | _ -> opts
        match thisObj with
        | Some (IgnoreSourcePos.Base as tv) ->
            this.CompileCall (info, opts, expr, Some (This |> WithSourcePosOfExpr tv), typ, meth, args, true)
        | _ ->
        if comp.HasGraph then
            this.AddMethodDependency(typ.Entity, meth.Entity)
        let trThisObj() = thisObj |> Option.map this.TransformExpression
        let trArgs() = 
            let ta = args
            match opts.FuncArgs with
            | Some ca ->
                (ca, ta) ||> Seq.map2 (fun ao expr ->
                    this.OptimizeArg(ao, expr) |> this.TransformExpression
                )
                |> List.ofSeq   
            | _ -> ta |> List.map this.TransformExpression
                        
        match info with
        | M.Instance name ->
            match baseCall with
            | Some true ->
                match comp.TryLookupClassInfo(typ.Entity).Value.Address with
                | Some ba ->
                    Application(
                        GlobalAccess ba |> getItem "prototype" |> getItem name |> getItem "call",
                        This :: (trArgs()), opts.Purity, None)
                | _ ->
                    this.Error("Cannot translate base call, prototype not found.")
            | _ ->
                Application(
                    trThisObj() |> Option.get |> getItem name,
                    trArgs(), opts.Purity, None) 
        | M.Static address ->
            // for methods compiled as static because of Prototype(false)
            let trThisArg = trThisObj() |> Option.toList
            Application(GlobalAccess address, trThisArg @ trArgs(), opts.Purity, Some meth.Entity.Value.Parameters.Length)
        | M.Inline ->
            Substitution(trArgs(), ?thisObj = trThisObj()).TransformExpression(expr)
        | M.NotCompiledInline ->
            let ge =
                if not (List.isEmpty typ.Generics && List.isEmpty meth.Generics) then
                    try GenericInlineResolver(typ.Generics @ meth.Generics).TransformExpression expr
                    with e -> this.Error (sprintf "Failed to resolve generics: %s" e.Message)
                else expr
            Substitution(trArgs(), ?thisObj = trThisObj()).TransformExpression(ge)
            |> this.TransformExpression
        | M.Macro (macro, parameter, fallback) ->
            let macroResult = 
                match comp.GetMacroInstance(macro) with
                | Some m ->
                    try 
                        m.TranslateCall {
                                This = thisObj
                                DefiningType = typ
                                Method = meth
                                Arguments = args
                                Parameter = parameter |> Option.map M.ParameterObject.ToObj
                                IsInline = currentIsInline
                                Compilation = comp
                        }
                    with e -> MacroError e.Message 
                | _ -> 
                    if comp.UseLocalMacros then
                        MacroError "Macro type failed to load"
                    else
                        MacroWarning(
                            "Cannot run macro in code service, consider moving it to another assembly.",
                            MacroOk Undefined
                        )
            let rec getExpr mres =
                match mres with
                | MacroOk resExpr -> this.TransformExpression resExpr
                | MacroWarning (msg, mres) ->
                    this.Warning (sprintf "Macro warning in %s.TranslateCall: %s" macro.Value.FullName msg)
                    getExpr mres
                | MacroError msg ->
                    this.Error(sprintf "Macro error in %s.TranslateCall: %s" macro.Value.FullName msg)
                | MacroDependencies (nodes, mres) ->
                    if comp.HasGraph then
                        nodes |> List.iter this.AddDependency
                    getExpr mres
                | MacroFallback ->
                    match fallback with
                    | None -> this.Error(sprintf "No macro fallback found for '%s'" macro.Value.FullName)
                    | Some f -> this.CompileCall (f, opts, expr, thisObj, typ, meth, args)      
                | MacroNeedsResolvedTypeArg t -> 
                    if currentIsInline then
                        hasDelayedTransform <- true
                        let typ = Generic (comp.FindProxied typ.Entity) typ.Generics
                        Call(trThisObj(), typ, meth, trArgs())
                    else 
                        this.HandleMacroNeedsResolvedTypeArg(t, macro.Value.FullName)
            getExpr macroResult
        | M.Remote (kind, handle, rh) ->
            let name, mnode =
                match kind with
                | M.RemoteAsync -> "Async", asyncRpcMethodNode
                | M.RemoteTask -> "Task", taskRpcMethodNode
                | M.RemoteSend -> "Send", sendRpcMethodNode
                | M.RemoteSync -> "Sync", syncRpcMethodNode
            let remotingProvider =
                let rpTyp, rpArgs =
                    match rh with
                    | Some (rp, p) -> 
                        rp, 
                        let toParamValue o = o |> M.ParameterObject.ToObj |> ReadLiteral |> Value
                        match p with
                        | None -> []
                        | Some (M.ParameterObject.Array ps) ->
                            ps |> Seq.map toParamValue |> List.ofSeq   
                        | Some p ->
                            [ toParamValue p ]
                    | _ -> defaultRemotingProvider   
                this.TransformCtor(NonGeneric rpTyp, emptyConstructor, rpArgs) 
            if comp.HasGraph then
                this.AddDependency(mnode)
                let rec addTypeDeps (t: Type) =
                    match t with
                    | ConcreteType c ->
                        this.AddDependency(M.TypeNode c.Entity)
                        c.Generics |> List.iter addTypeDeps
                    | ArrayType(t, _) -> addTypeDeps t
                    | TupleType (ts, _) -> ts |> List.iter addTypeDeps
                    | _ -> ()
                addTypeDeps meth.Entity.Value.ReturnType
            Application (remotingProvider |> getItem name, [ Value (String (handle.Pack())); NewArray (trArgs()) ], opts.Purity, Some 2)
        | M.Constructor _ -> failwith "Not a valid method info: Constructor"

    override this.TransformCall (thisObj, typ, meth, args) =
        if typ.Entity = Definitions.Dynamic then
            match meth.Entity.Value.MethodName with
            | BinaryOpName op ->
                if args.Length <> 2 then
                    this.Error("Dynamic binary operator expecting 2 arguments")
                else
                    Binary (this.TransformExpression args.[0], op, this.TransformExpression args.[1])
            | UnaryOpName op ->
                if args.Length <> 2 then
                    this.Error("Dynamic binary operator expecting 1 argument")
                else
                    Unary (op, this.TransformExpression args.[0])
            | "op_Decrement" ->
                if args.Length <> 1 then
                    this.Error("Dynamic decrement operator expecting 1 argument")
                else
                    Binary (this.TransformExpression args.[0], BinaryOperator.``-``, Value (Int 1))
            | "op_Increment" ->
                if args.Length <> 1 then
                    this.Error("Dynamic increment operator expecting 1 argument")
                else
                    Binary (this.TransformExpression args.[0], BinaryOperator.``+``, Value (Int 1))
            | n ->
                match thisObj with
                | Some o ->
                    Application(ItemGet(this.TransformExpression o, Value (String n), NonPure), args |> List.map this.TransformExpression, NonPure, None) 
                | _ ->
                    this.Error("Static method on dynamic object not tranlated: " + n)
        else
        match comp.LookupMethodInfo(typ.Entity, meth.Entity) with
        | Compiled (info, opts, expr) ->
            this.CompileCall(info, opts, expr, thisObj, typ, meth, args)
        | Compiling (info, expr) ->
            if isInline info then
                this.AnotherNode().CompileMethod(info, expr, typ.Entity, meth.Entity)
                this.TransformCall (thisObj, typ, meth, args)
            else
                match info with
                | NotCompiled (info, _, opts) ->
                    this.CompileCall(info, opts, expr, thisObj, typ, meth, args)
                | NotGenerated (_, _, info, _, _) ->
                    this.CompileCall(info, M.Optimizations.None, expr, thisObj, typ, meth, args)
        | CustomTypeMember ct ->  
            try
                this.CustomTypeMethod(thisObj, typ, ct, meth, args)
            with e ->
                this.Error(sprintf "Failed to translate compiler generated method: %s.%s - %s" typ.Entity.Value.FullName meth.Entity.Value.MethodName e.Message)
        | LookupMemberError err ->
            comp.AddError (this.CurrentSourcePos, err)
            match thisObj with 
            | Some thisObj ->
                Application(ItemGet(this.TransformExpression thisObj, errorPlaceholder, NonPure), args |> List.map this.TransformExpression, NonPure, None) 
            | _ ->
                Application(errorPlaceholder, args |> List.map this.TransformExpression, NonPure, None)

    override this.TransformTraitCall(typs, meth, args) =
        let mutable err = None
        let hasErr e =
            err <- match err with | Some p -> Some (p + "; " + e) | _ -> Some e
            None
        let mName = meth.Entity.Value.MethodName
        let res =
            typs |> List.tryPick (fun typ ->
                match typ with
                | ConcreteType ct ->
                    let ms =                    
                        comp.GetMethods ct.Entity |> Seq.choose (fun m ->
                            // TODO: check compatility with signature better
                            if m.Value.MethodName = mName then Some m else None
                        ) 
                        |> List.ofSeq
                    match ms with
                    | [ m ] ->
                        match args with
                        | t :: h ->
                            this.TransformCall(Some t, ct, Generic m meth.Generics, h) |> Some
                        | _ ->
                            failwith "Impossible: trait call without arguments"
                    | [] -> hasErr (sprintf "Could not find method for trait call: %s" mName) // (methods |> Seq.map (fun m -> m.Value.MethodName) |> String.concat ", "))
                    | _ -> hasErr (sprintf "Ambiguity at translating trait call: %s" mName)
                | _ ->
                    if currentIsInline then
                        hasDelayedTransform <- true
                        TraitCall(typs, meth, args |> List.map this.TransformExpression) |> Some
                    else 
                        hasErr("Using a trait call requires the Inline attribute")
            )
        match res with
        | Some ok -> ok
        | _ -> 
            match err with
            | None -> this.Error "Trait call has no source types"
            | Some e -> this.Error (e + "; types: " + (typs |> List.map string |> String.concat ", "))

    override this.TransformNewDelegate(thisObj, typ, meth) =
        // TODO: CustomTypeMember
        if comp.HasGraph then
            this.AddMethodDependency(typ.Entity, meth.Entity)
        let inlined() =
            let args = meth.Entity.Value.Parameters |> List.map (fun _ -> Id.New(mut = false))
            let call = 
                Lambda(args, Call(thisObj, typ, meth, args |> List.map Var))
                |> this.TransformExpression
            this.Warning("Creating delegate from inlined call, equality may not work.")
            call        
        match comp.LookupMethodInfo(typ.Entity, meth.Entity) with
        | Compiled (info, _, _)
        | Compiling ((NotCompiled (info, _, _) | NotGenerated (_, _, info, _, _)), _) ->
            match info with 
            | M.Static address -> 
                GlobalAccess address
            | M.Instance name -> 
                match comp.TryLookupClassInfo typ.Entity with
                | Some { Address = Some addr } ->
                    let func = GlobalAccess addr |> getItem "prototype" |> getItem name
                    JSRuntime.BindDelegate func (this.TransformExpression thisObj.Value) 
                | _ -> this.Error ("Cannot look up prototype for delegate creating")
            | M.NotCompiledInline
            | M.Inline _ 
            | M.Macro _ 
            | M.Remote _ -> inlined()
            | M.Constructor _ -> failwith "impossible"
        | CustomTypeMember _ -> inlined()
        | LookupMemberError err -> this.Error err

    member this.CompileCtor(info, opts: M.Optimizations, expr, typ, ctor, args) =
        if comp.HasGraph then
            this.AddConstructorDependency(typ.Entity, ctor)
        let trArgs() = 
            match opts.FuncArgs with
            | Some ca ->
                (ca, args) ||> Seq.map2 (fun ao expr ->
                    this.OptimizeArg(ao, expr) |> this.TransformExpression
                )
                |> List.ofSeq   
            | _ -> args |> List.map this.TransformExpression
        match info with
        | M.Constructor address ->
            New(GlobalAccess address, trArgs())
        | M.Static address ->
            Application(GlobalAccess address, trArgs(), opts.Purity, Some ctor.Value.CtorParameters.Length)
        | M.Inline -> 
            Substitution(trArgs()).TransformExpression(expr)
        | M.NotCompiledInline -> 
            let ge =
                if not (List.isEmpty typ.Generics) then
                    try GenericInlineResolver(typ.Generics).TransformExpression expr
                    with e -> this.Error(sprintf "Failed to resolve generics: %s" e.Message)
                else expr
            Substitution(trArgs()).TransformExpression(ge)
            |> this.TransformExpression
        | M.Macro (macro, parameter, fallback) ->
            let macroResult = 
                match comp.GetMacroInstance(macro) with
                | Some m ->
                    try
                        m.TranslateCtor {
                             DefiningType = typ
                             Constructor = ctor
                             Arguments = args
                             Parameter = parameter |> Option.map M.ParameterObject.ToObj
                             IsInline = currentIsInline
                             Compilation = comp
                        }
                    with e -> MacroError e.Message 
                | _ -> MacroError "Macro type failed to load"
            let rec getExpr mres =
                match mres with
                | MacroOk resExpr -> this.TransformExpression resExpr
                | MacroWarning (msg, mres) ->
                    this.Warning (sprintf "Macro warning in %s.TranslateCall: %s" macro.Value.FullName msg)
                    getExpr mres
                | MacroError msg ->
                    this.Error(sprintf "Macro error in %s.TranslateCall: %s" macro.Value.FullName msg)
                | MacroDependencies (nodes, mres) ->
                    if comp.HasGraph then
                        nodes |> List.iter this.AddDependency
                    getExpr mres
                | MacroFallback ->
                    match fallback with
                    | None -> this.Error(sprintf "No macro fallback found for '%s'" macro.Value.FullName)
                    | Some f -> this.CompileCtor (f, opts, expr, typ, ctor, args)      
                | MacroNeedsResolvedTypeArg t -> 
                    if currentIsInline then
                        hasDelayedTransform <- true
                        let typ = Generic (comp.FindProxied typ.Entity) typ.Generics
                        Ctor(typ, ctor, trArgs())
                    else 
                        this.HandleMacroNeedsResolvedTypeArg(t, macro.Value.FullName)
            getExpr macroResult
        | _ -> this.Error("Invalid metadata for constructor.")

    override this.TransformCopyCtor(typ, objExpr) =
        match comp.TryLookupClassInfo typ |> Option.bind (fun c -> if c.HasWSPrototype then c.Address else None) with
        | Some a ->
            if comp.HasGraph then
                this.AddTypeDependency typ
            New (GlobalAccess a, [ this.TransformExpression objExpr ])
        | _ -> this.TransformExpression objExpr

    override this.TransformNewRecord(typ, args) =
        match comp.TryGetRecordConstructor typ.Entity with
        | Some rctor ->
            if comp.HasGraph then
                this.AddDependency(M.ConstructorNode (comp.FindProxied typ.Entity, rctor))
            this.TransformCtor(typ, rctor, args)
        | _ ->
        match comp.GetCustomType typ.Entity with
        | M.FSharpRecordInfo fields ->
            let obj = 
                (args, fields)
                ||> Seq.map2 (fun a f -> 
                    f.JSName,
                        if f.Optional then
                            let id = Id.New(mut = false)
                            Let(id, this.TransformExpression a,
                                Conditional(Var id, ItemGet(Var id, Value (String "$0"), Pure), Undefined))
                        else this.TransformExpression a)
                |> List.ofSeq |> Object
            let optFields = 
                fields |> List.choose (fun f -> 
                    if f.Optional then Some (Value (String f.JSName)) else None)
            if List.isEmpty optFields then obj
            else JSRuntime.DeleteEmptyFields obj optFields
        | _ -> this.Error("Unhandled F# compiler generated constructor")

    override this.TransformNewUnionCase(typ, case, args) = 
        let td = typ.Entity
        if erasedUnions.Contains td then
            match args with
            | [] -> Undefined
            | [ a ] -> this.TransformExpression a
            | _ -> this.Error("Erased union constructor expects a single argument")
        else
        match comp.GetCustomType td with
        | M.FSharpUnionInfo u ->
            let i, c = 
                try
                    u.Cases |> Seq.indexed |> Seq.find (fun (_, c) -> c.Name = case)
                with _ ->
                    failwithf "Failed to find union case constructor %s in %s, found: %s" case td.Value.FullName (u.Cases |> Seq.map (fun c -> c.Name) |> String.concat ", ")
            match c.Kind with
            | M.ConstantFSharpUnionCase v ->
                Value v
            | M.SingletonFSharpUnionCase -> 
                match comp.TryLookupClassInfo td |> Option.bind (fun cls -> cls.Address) with
                | Some a -> 
                    let caseField = Definitions.SingletonUnionCase case
                    if comp.HasGraph then
                        this.AddMethodDependency(td, caseField)
                    ItemGet(GlobalAccess a, Value (String case), Pure)
                | None -> this.Error("Failed to find address for singleton union case.")
            | M.NormalFSharpUnionCase _ ->
                let objExpr =
                    Object (
                        ("$", Value (Int i)) ::
                        (args |> List.mapi (fun j e -> "$" + string j, e)) 
                    )
                this.TransformCopyCtor(td, objExpr)
        | _ -> this.Error("Failed to translate union case creation.")

    override this.TransformUnionCaseTest(expr, typ, case) = 
        if erasedUnions.Contains typ.Entity then
            match case with 
            | "Undefined" -> this.TransformExpression expr ^=== Undefined
            | "Defined" -> this.TransformExpression expr ^!== Undefined
            | _ ->
            let i = int case.[5] - 49 // int '1' is 49
            try
                let t = typ.Generics.[i]
                match this.GetTypeCheckKind t with
                | PlainObject ->
                    let prevCases =
                        List.init i (fun j ->
                            this.GetTypeCheckKind (typ.Generics.[j]) 
                        )
                    let prevCasesTranslating =
                        prevCases |> List.forall (function 
                            | TypeOf _ | InstanceOf _ | IsNull -> true 
                            | _ -> false
                        )
                    if prevCasesTranslating then 
                        (this.TransformExpression expr |> getItem "constructor") ^=== (Global ["Object"])
                    else
                        this.Error (sprintf "Translating erased union test failed, case: %s, more than one plain object type found" case)
                | _ -> 
                    this.TransformTypeCheck(expr, t)
            with e ->
                this.Error(sprintf "Translating erased union test failed, case: %s, generics: %A"
                    case (typ.Generics |> List.map (fun t -> t.AssemblyQualifiedName)))
        else
        match comp.GetCustomType typ.Entity with
        | M.FSharpUnionInfo u ->
            let i, c = u.Cases |> Seq.indexed |> Seq.find (fun (i, c) -> c.Name = case)
            match c.Kind with
            | M.ConstantFSharpUnionCase v ->
                this.TransformExpression expr ^== Value v
            | _ -> 
                if u.HasNull then
                    let v = Id.New(mut = false)
                    Let (v, this.TransformExpression expr, 
                        (Var v ^!= Value Null) ^&& (ItemGet(Var v, Value (String "$"), Pure) ^== Value (Int i)) 
                    )
                else
                    ItemGet(this.TransformExpression expr, Value (String "$"), Pure) ^== Value (Int i)    
        | _ -> this.Error("Failed to translate union case test.")
    
    override this.TransformUnionCaseGet(expr, typ, case, field) =
        if erasedUnions.Contains typ.Entity then
            this.TransformExpression expr
        else
        match comp.GetCustomType typ.Entity with
        | M.FSharpUnionInfo u ->
            let i, c = u.Cases |> Seq.indexed |> Seq.find (fun (_, c) -> c.Name = case)
            match c.Kind with
            | M.ConstantFSharpUnionCase _ ->
                this.Error(sprintf "Getting item of Constant union case: %s.%s" typ.Entity.Value.FullName case) 
            | M.SingletonFSharpUnionCase ->
                this.Error(sprintf "Getting item of argumentless union case: %s.%s" typ.Entity.Value.FullName case) 
            | M.NormalFSharpUnionCase fields -> 
                match fields |> List.tryFindIndex (fun f -> f.Name = field) with
                | Some i ->
                    this.TransformExpression expr |> getItem ("$" + string i)
                | _ ->
                    this.Error(sprintf "Could not find field of union case: %s.%s.%s" typ.Entity.Value.FullName case field)        
        
        | _ -> this.Error("Failed to translate union case field getter.")

    override this.TransformUnionCaseTag(expr, typ) = 
        if erasedUnions.Contains typ.Entity then
            if typ.Entity.Value.FullName = "WebSharper.JavaScript.Optional`1" then
                Conditional(this.TransformExpression expr ^=== Undefined, Value (Int 0), Value (Int 1))
            else
                let id = Id.New(mut = false)
                let rec checkTypes i gen =
                    match gen with
                    | [ t; _ ] ->
                        Conditional(this.TransformTypeCheck(Var id, t), Value (Int i), Value (Int (i + 1))) 
                    | t :: r ->
                        Conditional(this.TransformTypeCheck(Var id, t), Value (Int i), checkTypes (i + 1) r) 
                    | _ -> this.Error "Erased union type must have 2 or more type arguments"
                Let(id, this.TransformExpression expr, checkTypes 0 typ.Generics)
        else
        match comp.GetCustomType typ.Entity with
        | M.FSharpUnionInfo u ->
            let constantCases = 
                u.Cases |> List.indexed |> List.filter (function (_, { Kind = M.ConstantFSharpUnionCase _ }) -> true | _ -> false)
            if List.isEmpty constantCases then                 
                this.TransformExpression expr |> getItem "$"
            else 
                // TODO: no default tag when all cases are constant valued
                let ev = Id.New (mut = false)
                let b = 
                    (constantCases, Var ev |> getItem "$")
                    ||> List.foldBack (fun (i, c) e -> 
                        match c.Kind with
                        | M.ConstantFSharpUnionCase v ->
                            Conditional(Var ev ^== Value v, Value (Int i), e)
                        | _ -> failwith "impossible"
                    )   
                Let (ev, this.TransformExpression expr, b)
        | _ -> this.Error("Failed to translate union case tag.")

    override this.TransformCtor(typ, ctor, args) =
        let node = comp.LookupConstructorInfo(typ.Entity, ctor)
        match node with
        | Compiled (info, opts, expr) -> 
            this.CompileCtor(info, opts, expr, typ, ctor, args)
        | Compiling (info, expr) ->
            if isInline info then
                this.AnotherNode().CompileConstructor(info, expr, typ.Entity, ctor)
                this.TransformCtor(typ, ctor, args)
            else 
                match info with
                | NotCompiled (info, _, opts) -> 
                    this.CompileCtor(info, opts, expr, typ, ctor, args)
                | NotGenerated (_, _, info, _, _) ->
                    this.CompileCtor(info, M.Optimizations.None, expr, typ, ctor, args)
        | CustomTypeMember ct ->  
            try
                this.CustomTypeConstructor(typ, ct, ctor, args)
            with _ ->
                this.Error("Failed to translate compiler generated constructor")
        | LookupMemberError err ->
            comp.AddError (this.CurrentSourcePos, err)
            Application(errorPlaceholder, args |> List.map this.TransformExpression, NonPure, None)
                  
    override this.TransformBaseCtor(expr, typ, ctor, args) =
        let norm = this.TransformCtor(typ, ctor, args)
        let def () =
            match norm with
            | New (func, a) ->
                Application(func |> getItem "call", expr :: a, NonPure, None)
            // This is allowing some simple inlines
            | Let (i1, a1, New(func, [Var v1])) when i1 = v1 ->
                Application(func |> getItem "call", expr :: [a1], NonPure, None)
            | _ ->
                comp.AddError (this.CurrentSourcePos, SourceError "Chained constructor is an Inline in a not supported form")
                Application(errorPlaceholder, args |> List.map this.TransformExpression, NonPure, None)
        if currentIsInline then
            match IgnoreExprSourcePos expr with
            | This -> norm
            | Var _ -> def()
            | _ -> this.Error("Unrecognized this value in constructor inline")
        else def()

    override this.TransformCctor(typ) =
        match comp.TryLookupStaticConstructorAddress typ with
        | Some cctor ->
            if comp.HasGraph then
                this.AddTypeDependency typ
            Application(GlobalAccess cctor, [], NonPure, Some 0)
        | None -> Undefined

    override this.TransformOverrideName(typ, meth) =
        match comp.LookupMethodInfo(typ, meth) with
        | Compiled (M.Instance name, _, _) 
        | Compiling ((NotCompiled ((M.Instance name), _, _) | NotGenerated (_,_,M.Instance name, _, _)), _) ->
            Value (String name)
        | LookupMemberError err ->
            this.Error err
        | _ -> 
            this.Error ("Could not get name of abstract method")

    override this.TransformSelf () = 
        match selfAddress with
        | Some self -> GlobalAccess self
        | _ -> this.Error ("Self address missing")

    override this.TransformFieldGet (expr, typ, field) =
        if comp.HasGraph then
            this.AddTypeDependency typ.Entity
        match comp.LookupFieldInfo (typ.Entity, field) with
        | CompiledField (f, ro, _) ->
            match f with
            | M.InstanceField fname ->
                this.TransformExpression expr.Value |> getItemRO fname ro
            | M.StaticField faddr ->
                match comp.TryLookupStaticConstructorAddress typ.Entity with
                | Some cctorAddr ->
                    Sequential [
                        Application(GlobalAccess cctorAddr, [], NonPure, Some 0)
                        GlobalAccess faddr
                    ]
                | _ ->    
                    GlobalAccess faddr   
            | M.OptionalField fname -> 
                JSRuntime.GetOptional (this.TransformExpression expr.Value |> getItem fname)
            | M.IndexedField i ->
                this.TransformExpression expr.Value |> getIndexRO i ro
        | CustomTypeField ct ->
            match ct with
            | M.FSharpUnionCaseInfo case ->
                match case.Kind with
                | M.NormalFSharpUnionCase fields ->
                    let fName = "$" + string (fields |> List.findIndex (fun f -> f.Name = field))
                    this.TransformExpression expr.Value |> getItem fName
                | _ -> this.Error "Constant union case should not have fields" 
            | M.FSharpRecordInfo fields ->
                match fields |> List.tryPick (fun f -> if f.Name = field then Some (f.JSName, f.Optional, not f.IsMutable) else None) with
                | Some (name, isOpt, ro) ->
                    if isOpt then
                        JSRuntime.GetOptional (this.TransformExpression expr.Value |> getItem name)
                    else
                        this.TransformExpression expr.Value |> getItemRO name ro
                | _ -> this.Error(sprintf "Could not find field of F# record type: %s.%s" typ.Entity.Value.FullName field)
            | M.FSharpUnionInfo _ -> this.Error "Union base type should not have fields"   
            | _ -> failwith "CustomTypeField error"          
        | PropertyField (getter, _) ->
            match getter with
            | Some m -> 
                this.TransformCall (expr, typ, NonGeneric m, [])   
            | _ -> this.Error(sprintf "Could not getter of F# field: %s.%s" typ.Entity.Value.FullName field)
        | LookupFieldError err ->
            this.Error err

    override this.TransformFieldSet (expr, typ, field, value) =
        if comp.HasGraph then
            this.AddTypeDependency typ.Entity
        match comp.LookupFieldInfo (typ.Entity, field) with
        | CompiledField (f, _, _) ->
            match f with
            | M.InstanceField fname ->
                ItemSet(this.TransformExpression expr.Value, Value (String fname), this.TransformExpression value) 
            | M.StaticField faddr ->
                let f, a = List.head faddr.Value, List.tail faddr.Value
                match comp.TryLookupStaticConstructorAddress typ.Entity with
                | Some cctorAddr ->
                    Sequential [
                        Application(GlobalAccess cctorAddr, [], NonPure, Some 0)
                        ItemSet(GlobalAccess (Hashed a), Value (String f), this.TransformExpression value)
                    ]
                | _ ->    
                    ItemSet(GlobalAccess (Hashed a), Value (String f), this.TransformExpression value)
            | M.OptionalField fname -> 
                JSRuntime.SetOptional (this.TransformExpression expr.Value) (Value (String fname)) (this.TransformExpression value)
            | M.IndexedField i ->
                ItemSet(this.TransformExpression expr.Value, Value (Int i), this.TransformExpression value) 
        | CustomTypeField ct ->
            match ct with
            | M.FSharpRecordInfo fields ->
                match fields |> List.tryPick (fun f -> if f.Name = field then Some (f.JSName, f.Optional) else None) with
                | Some (name, isOpt) ->
                    if isOpt then
                        JSRuntime.SetOptional (this.TransformExpression expr.Value) (Value (String name)) (this.TransformExpression value)
                    else
                        ItemSet(this.TransformExpression expr.Value, Value (String name), this.TransformExpression value)
                | _ -> this.Error(sprintf "Could not find field of F# record type: %s.%s" typ.Entity.Value.FullName field)
            | M.FSharpUnionCaseInfo _ -> this.Error "Union case field should not be set" 
            | M.FSharpUnionInfo _ -> this.Error "Union base type should not have fields"   
            | _ -> failwith "CustomTypeField error"          
        | PropertyField (_, setter) ->
            match setter with
            | Some m -> 
                this.TransformCall (expr, typ, NonGeneric m, [value])   
            | _ -> this.Error(sprintf "Could not find setter of property: %s.%s" typ.Entity.Value.FullName field)
        | LookupFieldError err ->
            comp.AddError (this.CurrentSourcePos, err)
            ItemSet(errorPlaceholder, errorPlaceholder, this.TransformExpression value)

    member this.GetTypeCheckKind typ =
        match typ with
        | ConcreteType { Entity = t; Generics = gs } ->
            match t.Value.FullName with
            | "System.Void" ->                                                                
                TypeOf "undefined"
            | "Microsoft.FSharp.Core.Unit" ->
                IsNull  
            | "WebSharper.JavaScript.Object" ->
                TypeOf "object"
            | "WebSharper.JavaScript.Boolean"
            | "System.Boolean" ->
                TypeOf "boolean"
            | "WebSharper.JavaScript.Number"
            | "System.Byte"
            | "System.SByte"
            | "System.Char"
            | "System.Single"
            | "System.Double"
            | "System.Int16"
            | "System.Int32"
            | "System.Int64"
            | "System.UInt16"
            | "System.UInt32"
            | "System.UInt64" ->
                TypeOf "number"
            | "System.String" ->
                TypeOf "string"
            | "WebSharper.JavaScript.Error"
            | "System.Exception" ->
                InstanceOf (Address ["Error"])
            | "WebSharper.JavaScript.Array"
            | "System.Array" ->
                InstanceOf (Address ["Array"])
            | "WebSharper.JavaScript.Function" ->
                TypeOf "function"
            | _ ->
                match comp.TryLookupClassAddressOrCustomType t with
                | Choice1Of2 (Some a) ->
                    InstanceOf a
                | Choice1Of2 None ->
                    PlainObject
                | Choice2Of2 ct -> 
                    match ct with
                    | M.DelegateInfo _ ->
                        TypeOf "function"
                    | M.FSharpRecordInfo _
                    | M.FSharpUnionInfo _
                    | M.FSharpUnionCaseInfo _
                    | M.StructInfo _ ->
                        PlainObject
                    | _ ->
                        OtherTypeCheck
        | _ ->
            OtherTypeCheck 

    override this.TransformTypeCheck(expr, typ) =
        match typ with
        | ConcreteType td ->
            if comp.HasGraph then
                this.AddTypeDependency td.Entity
        | _ -> ()
        let trExpr = this.TransformExpression expr
        match tryGetTypeCheck (this.GetTypeCheckKind typ) trExpr with
        | Some res -> res
        | _ ->
        match typ with
        | ConcreteType { Entity = t; Generics = gs } ->
            match t.Value.FullName with
            | "System.IDisposable" ->
                Binary(
                    Value (String "Dispose"),
                    BinaryOperator.``in``,
                    trExpr
                )
            | "Microsoft.FSharp.Core.FSharpChoice`2"
            | "Microsoft.FSharp.Core.FSharpChoice`3"
            | "Microsoft.FSharp.Core.FSharpChoice`4"
            | "Microsoft.FSharp.Core.FSharpChoice`5"
            | "Microsoft.FSharp.Core.FSharpChoice`6"
            | "Microsoft.FSharp.Core.FSharpChoice`7" ->
                trExpr
            | tname ->
                if not (List.isEmpty gs) then
                    this.Warning ("Type test in JavaScript translation is ignoring erased type parameter.")
                match comp.TryLookupClassAddressOrCustomType t with
                | Choice1Of2 (Some a) ->
                    Binary(trExpr, BinaryOperator.instanceof, GlobalAccess a)
                | Choice1Of2 None ->
                    this.Error("Type test cannot be translated because client-side class does not have a prototype, add the Prototype attribute to it: " + t.Value.FullName)
                | Choice2Of2 ct -> 
                    match ct with
                    | M.FSharpUnionCaseInfo c ->
                        let tN = t.Value.FullName
                        let nestedIn = tN.[.. tN.LastIndexOf '+' - 1]
                        let uTyp = { Entity = TypeDefinition { t.Value with FullName = nestedIn } ; Generics = [] } 
                        let i = Id.New (mut = false)
                        Let (i, trExpr, this.TransformTypeCheck(Var i, ConcreteType uTyp) ^&& this.TransformUnionCaseTest(Var i, uTyp, c.Name)) 
                    | _ -> 
                        this.Error(sprintf "Failed to compile a type check for type '%s'" tname)
        | TypeParameter _ | StaticTypeParameter _ -> 
            if currentIsInline then
                hasDelayedTransform <- true
                TypeCheck(trExpr, typ)
            else 
                this.Error("Using a type test on a type parameter requires the Inline attribute.")
        | ArrayType _ -> this.Error("Type tests do not support generic array type, check against System.Array.")
        | FSharpFuncType _ -> this.Error("Type tests do not support F# function type, check against WebSharper.JavaScript.Function.")   
        | _ ->  this.Error("Failed to compile a type check.")
