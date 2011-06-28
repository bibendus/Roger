%% The contents of this file are subject to the Mozilla Public License
%% Version 1.1 (the "License"); you may not use this file except in
%% compliance with the License. You may obtain a copy of the License
%% at http://www.mozilla.org/MPL/
%%
%% Software distributed under the License is distributed on an "AS IS"
%% basis, WITHOUT WARRANTY OF ANY KIND, either express or implied. See
%% the License for the specific language governing rights and
%% limitations under the License.
%%
%% The Original Code is RabbitMQ.
%%
%% The Initial Developer of the Original Code is VMware, Inc.
%% Copyright (c) 2007-2011 VMware, Inc.  All rights reserved.
%%

-module(rabbit_control).
-include("rabbit.hrl").

-export([start/0, stop/0, action/5, diagnostics/1]).

-define(RPC_TIMEOUT, infinity).
-define(WAIT_FOR_VM_ATTEMPTS, 5).

-define(QUIET_OPT, "-q").
-define(NODE_OPT, "-n").
-define(VHOST_OPT, "-p").

-define(GLOBAL_QUERIES,
        [{"Connections", rabbit_networking, connection_info_all,
          connection_info_keys},
         {"Channels",  rabbit_channel,  info_all, info_keys}]).

-define(VHOST_QUERIES,
        [{"Queues",    rabbit_amqqueue, info_all, info_keys},
         {"Exchanges", rabbit_exchange, info_all, info_keys},
         {"Bindings",  rabbit_binding,  info_all, info_keys},
         {"Consumers", rabbit_amqqueue, consumers_all, consumer_info_keys},
         {"Permissions", rabbit_auth_backend_internal, list_vhost_permissions,
          vhost_perms_info_keys}]).

%%----------------------------------------------------------------------------

-ifdef(use_specs).

-spec(start/0 :: () -> no_return()).
-spec(stop/0 :: () -> 'ok').
-spec(action/5 ::
        (atom(), node(), [string()], [{string(), any()}],
         fun ((string(), [any()]) -> 'ok'))
        -> 'ok').
-spec(diagnostics/1 :: (node()) -> [{string(), [any()]}]).
-spec(usage/0 :: () -> no_return()).

-endif.

%%----------------------------------------------------------------------------

start() ->
    {ok, [[NodeStr|_]|_]} = init:get_argument(nodename),
    {[Command0 | Args], Opts} =
        case rabbit_misc:get_options([{flag, ?QUIET_OPT},
                                      {option, ?NODE_OPT, NodeStr},
                                      {option, ?VHOST_OPT, "/"}],
                                     init:get_plain_arguments()) of
            {[], _Opts}    -> usage();
            CmdArgsAndOpts -> CmdArgsAndOpts
        end,
    Opts1 = [case K of
                 ?NODE_OPT -> {?NODE_OPT, rabbit_misc:makenode(V)};
                 _         -> {K, V}
             end || {K, V} <- Opts],
    Command = list_to_atom(Command0),
    Quiet = proplists:get_bool(?QUIET_OPT, Opts1),
    Node = proplists:get_value(?NODE_OPT, Opts1),
    Inform = case Quiet of
                 true  -> fun (_Format, _Args1) -> ok end;
                 false -> fun (Format, Args1) ->
                                  io:format(Format ++ " ...~n", Args1)
                          end
             end,
    %% The reason we don't use a try/catch here is that rpc:call turns
    %% thrown errors into normal return values
    case catch action(Command, Node, Args, Opts, Inform) of
        ok ->
            case Quiet of
                true  -> ok;
                false -> io:format("...done.~n")
            end,
            quit(0);
        {'EXIT', {function_clause, [{?MODULE, action, _} | _]}} ->
            print_error("invalid command '~s'",
                        [string:join([atom_to_list(Command) | Args], " ")]),
            usage();
        {error, Reason} ->
            print_error("~p", [Reason]),
            quit(2);
        {badrpc, {'EXIT', Reason}} ->
            print_error("~p", [Reason]),
            quit(2);
        {badrpc, Reason} ->
            print_error("unable to connect to node ~w: ~w", [Node, Reason]),
            print_badrpc_diagnostics(Node),
            quit(2);
        Other ->
            print_error("~p", [Other]),
            quit(2)
    end.

fmt_stderr(Format, Args) -> rabbit_misc:format_stderr(Format ++ "~n", Args).

print_report(Node, {Descr, Module, InfoFun, KeysFun}) ->
    io:format("~s:~n", [Descr]),
    print_report0(Node, {Module, InfoFun, KeysFun}, []).

print_report(Node, {Descr, Module, InfoFun, KeysFun}, VHostArg) ->
    io:format("~s on ~s:~n", [Descr, VHostArg]),
    print_report0(Node, {Module, InfoFun, KeysFun}, VHostArg).

print_report0(Node, {Module, InfoFun, KeysFun}, VHostArg) ->
    case Results = rpc_call(Node, Module, InfoFun, VHostArg) of
        [_|_] -> InfoItems = rpc_call(Node, Module, KeysFun, []),
                 display_row([atom_to_list(I) || I <- InfoItems]),
                 display_info_list(Results, InfoItems);
        _     -> ok
    end,
    io:nl().

print_error(Format, Args) -> fmt_stderr("Error: " ++ Format, Args).

print_badrpc_diagnostics(Node) ->
    [fmt_stderr(Fmt, Args) || {Fmt, Args} <- diagnostics(Node)].

diagnostics(Node) ->
    {_NodeName, NodeHost} = rabbit_misc:nodeparts(Node),
    [{"diagnostics:", []},
     case net_adm:names(NodeHost) of
         {error, EpmdReason} ->
             {"- unable to connect to epmd on ~s: ~w",
              [NodeHost, EpmdReason]};
         {ok, NamePorts} ->
             {"- nodes and their ports on ~s: ~p",
              [NodeHost, [{list_to_atom(Name), Port} ||
                             {Name, Port} <- NamePorts]]}
     end,
     {"- current node: ~w", [node()]},
     case init:get_argument(home) of
         {ok, [[Home]]} -> {"- current node home dir: ~s", [Home]};
         Other          -> {"- no current node home dir: ~p", [Other]}
     end,
     {"- current node cookie hash: ~s", [rabbit_misc:cookie_hash()]}].

stop() ->
    ok.

usage() ->
    io:format("~s", [rabbit_ctl_usage:usage()]),
    quit(1).

%%----------------------------------------------------------------------------

action(stop, Node, [], _Opts, Inform) ->
    Inform("Stopping and halting node ~p", [Node]),
    call(Node, {rabbit, stop_and_halt, []});

action(stop_app, Node, [], _Opts, Inform) ->
    Inform("Stopping node ~p", [Node]),
    call(Node, {rabbit, stop, []});

action(start_app, Node, [], _Opts, Inform) ->
    Inform("Starting node ~p", [Node]),
    call(Node, {rabbit, start, []});

action(reset, Node, [], _Opts, Inform) ->
    Inform("Resetting node ~p", [Node]),
    call(Node, {rabbit_mnesia, reset, []});

action(force_reset, Node, [], _Opts, Inform) ->
    Inform("Forcefully resetting node ~p", [Node]),
    call(Node, {rabbit_mnesia, force_reset, []});

action(cluster, Node, ClusterNodeSs, _Opts, Inform) ->
    ClusterNodes = lists:map(fun list_to_atom/1, ClusterNodeSs),
    Inform("Clustering node ~p with ~p",
           [Node, ClusterNodes]),
    rpc_call(Node, rabbit_mnesia, cluster, [ClusterNodes]);

action(force_cluster, Node, ClusterNodeSs, _Opts, Inform) ->
    ClusterNodes = lists:map(fun list_to_atom/1, ClusterNodeSs),
    Inform("Forcefully clustering node ~p with ~p (ignoring offline nodes)",
           [Node, ClusterNodes]),
    rpc_call(Node, rabbit_mnesia, force_cluster, [ClusterNodes]);

action(wait, Node, [], _Opts, Inform) ->
    Inform("Waiting for ~p", [Node]),
    wait_for_application(Node, ?WAIT_FOR_VM_ATTEMPTS);

action(status, Node, [], _Opts, Inform) ->
    Inform("Status of node ~p", [Node]),
    display_call_result(Node, {rabbit, status, []});

action(cluster_status, Node, [], _Opts, Inform) ->
    Inform("Cluster status of node ~p", [Node]),
    display_call_result(Node, {rabbit_mnesia, status, []});

action(environment, Node, _App, _Opts, Inform) ->
    Inform("Application environment of node ~p", [Node]),
    display_call_result(Node, {rabbit, environment, []});

action(rotate_logs, Node, [], _Opts, Inform) ->
    Inform("Reopening logs for node ~p", [Node]),
    call(Node, {rabbit, rotate_logs, [""]});
action(rotate_logs, Node, Args = [Suffix], _Opts, Inform) ->
    Inform("Rotating logs to files with suffix ~p", [Suffix]),
    call(Node, {rabbit, rotate_logs, Args});

action(close_connection, Node, [PidStr, Explanation], _Opts, Inform) ->
    Inform("Closing connection ~s", [PidStr]),
    rpc_call(Node, rabbit_networking, close_connection,
             [rabbit_misc:string_to_pid(PidStr), Explanation]);

action(add_user, Node, Args = [Username, _Password], _Opts, Inform) ->
    Inform("Creating user ~p", [Username]),
    call(Node, {rabbit_auth_backend_internal, add_user, Args});

action(delete_user, Node, Args = [_Username], _Opts, Inform) ->
    Inform("Deleting user ~p", Args),
    call(Node, {rabbit_auth_backend_internal, delete_user, Args});

action(change_password, Node, Args = [Username, _Newpassword], _Opts, Inform) ->
    Inform("Changing password for user ~p", [Username]),
    call(Node, {rabbit_auth_backend_internal, change_password, Args});

action(clear_password, Node, Args = [Username], _Opts, Inform) ->
    Inform("Clearing password for user ~p", [Username]),
    call(Node, {rabbit_auth_backend_internal, clear_password, Args});

action(set_admin, Node, [Username], _Opts, Inform) ->
    Inform("Setting administrative status for user ~p", [Username]),
    call(Node, {rabbit_auth_backend_internal, set_admin, [Username]});

action(clear_admin, Node, [Username], _Opts, Inform) ->
    Inform("Clearing administrative status for user ~p", [Username]),
    call(Node, {rabbit_auth_backend_internal, clear_admin, [Username]});

action(list_users, Node, [], _Opts, Inform) ->
    Inform("Listing users", []),
    display_list(call(Node, {rabbit_auth_backend_internal, list_users, []}));

action(add_vhost, Node, Args = [_VHostPath], _Opts, Inform) ->
    Inform("Creating vhost ~p", Args),
    call(Node, {rabbit_vhost, add, Args});

action(delete_vhost, Node, Args = [_VHostPath], _Opts, Inform) ->
    Inform("Deleting vhost ~p", Args),
    call(Node, {rabbit_vhost, delete, Args});

action(list_vhosts, Node, Args, _Opts, Inform) ->
    Inform("Listing vhosts", []),
    ArgAtoms = default_if_empty(Args, [name]),
    display_info_list(call(Node, {rabbit_vhost, info_all, []}), ArgAtoms);

action(list_user_permissions, Node, Args = [_Username], _Opts, Inform) ->
    Inform("Listing permissions for user ~p", Args),
    display_info_list(call(Node, {rabbit_auth_backend_internal,
                                  list_user_permissions, Args}),
                      rabbit_auth_backend_internal:user_perms_info_keys());

action(list_queues, Node, Args, Opts, Inform) ->
    Inform("Listing queues", []),
    VHostArg = list_to_binary(proplists:get_value(?VHOST_OPT, Opts)),
    ArgAtoms = default_if_empty(Args, [name, messages]),
    display_info_list(rpc_call(Node, rabbit_amqqueue, info_all,
                               [VHostArg, ArgAtoms]),
                      ArgAtoms);

action(list_exchanges, Node, Args, Opts, Inform) ->
    Inform("Listing exchanges", []),
    VHostArg = list_to_binary(proplists:get_value(?VHOST_OPT, Opts)),
    ArgAtoms = default_if_empty(Args, [name, type]),
    display_info_list(rpc_call(Node, rabbit_exchange, info_all,
                               [VHostArg, ArgAtoms]),
                      ArgAtoms);

action(list_bindings, Node, Args, Opts, Inform) ->
    Inform("Listing bindings", []),
    VHostArg = list_to_binary(proplists:get_value(?VHOST_OPT, Opts)),
    ArgAtoms = default_if_empty(Args, [source_name, source_kind,
                                       destination_name, destination_kind,
                                       routing_key, arguments]),
    display_info_list(rpc_call(Node, rabbit_binding, info_all,
                               [VHostArg, ArgAtoms]),
                      ArgAtoms);

action(list_connections, Node, Args, _Opts, Inform) ->
    Inform("Listing connections", []),
    ArgAtoms = default_if_empty(Args, [user, peer_address, peer_port, state]),
    display_info_list(rpc_call(Node, rabbit_networking, connection_info_all,
                               [ArgAtoms]),
                      ArgAtoms);

action(list_channels, Node, Args, _Opts, Inform) ->
    Inform("Listing channels", []),
    ArgAtoms = default_if_empty(Args, [pid, user, transactional, consumer_count,
                                       messages_unacknowledged]),
    display_info_list(rpc_call(Node, rabbit_channel, info_all, [ArgAtoms]),
                      ArgAtoms);

action(list_consumers, Node, _Args, Opts, Inform) ->
    Inform("Listing consumers", []),
    VHostArg = list_to_binary(proplists:get_value(?VHOST_OPT, Opts)),
    display_info_list(rpc_call(Node, rabbit_amqqueue, consumers_all, [VHostArg]),
                      rabbit_amqqueue:consumer_info_keys());

action(trace_on, Node, [], Opts, Inform) ->
    VHost = proplists:get_value(?VHOST_OPT, Opts),
    Inform("Starting tracing for vhost ~p", [VHost]),
    rpc_call(Node, rabbit_trace, start, [list_to_binary(VHost)]);

action(trace_off, Node, [], Opts, Inform) ->
    VHost = proplists:get_value(?VHOST_OPT, Opts),
    Inform("Stopping tracing for vhost ~p", [VHost]),
    rpc_call(Node, rabbit_trace, stop, [list_to_binary(VHost)]);

action(set_permissions, Node, [Username, CPerm, WPerm, RPerm], Opts, Inform) ->
    VHost = proplists:get_value(?VHOST_OPT, Opts),
    Inform("Setting permissions for user ~p in vhost ~p", [Username, VHost]),
    call(Node, {rabbit_auth_backend_internal, set_permissions,
                [Username, VHost, CPerm, WPerm, RPerm]});

action(clear_permissions, Node, [Username], Opts, Inform) ->
    VHost = proplists:get_value(?VHOST_OPT, Opts),
    Inform("Clearing permissions for user ~p in vhost ~p", [Username, VHost]),
    call(Node, {rabbit_auth_backend_internal, clear_permissions,
                [Username, VHost]});

action(list_permissions, Node, [], Opts, Inform) ->
    VHost = proplists:get_value(?VHOST_OPT, Opts),
    Inform("Listing permissions in vhost ~p", [VHost]),
    display_info_list(call(Node, {rabbit_auth_backend_internal,
                             list_vhost_permissions, [VHost]}),
                      rabbit_auth_backend_internal:vhost_perms_info_keys());

action(report, Node, _Args, _Opts, Inform) ->
    io:format("Reporting server status on ~p~n~n", [erlang:universaltime()]),
    [begin ok = action(Action, N, [], [], Inform), io:nl() end ||
        N      <- unsafe_rpc(Node, rabbit_mnesia, running_clustered_nodes, []),
        Action <- [status, cluster_status, environment]],
    VHosts = unsafe_rpc(Node, rabbit_vhost, list, []),
    [print_report(Node, Q)      || Q <- ?GLOBAL_QUERIES],
    [print_report(Node, Q, [V]) || Q <- ?VHOST_QUERIES, V <- VHosts],
    io:format("End of server status report~n"),
    ok.

%%----------------------------------------------------------------------------

wait_for_application(Node, Attempts) ->
    case rpc_call(Node, application, which_applications, [infinity]) of
        {badrpc, _} = E -> case Attempts of
                               0 -> E;
                               _ -> wait_for_application0(Node, Attempts - 1)
                           end;
        Apps            -> case proplists:is_defined(rabbit, Apps) of
                               %% We've seen the node up; if it goes down
                               %% die immediately.
                               true  -> ok;
                               false -> wait_for_application0(Node, 0)
                           end
    end.

wait_for_application0(Node, Attempts) ->
    timer:sleep(1000),
    wait_for_application(Node, Attempts).

default_if_empty(List, Default) when is_list(List) ->
    if List == [] -> Default;
       true       -> [list_to_atom(X) || X <- List]
    end.

display_info_list(Results, InfoItemKeys) when is_list(Results) ->
    lists:foreach(
      fun (Result) -> display_row(
                        [format_info_item(proplists:get_value(X, Result)) ||
                            X <- InfoItemKeys])
      end, Results),
    ok;
display_info_list(Other, _) ->
    Other.

display_row(Row) ->
    io:fwrite(string:join(Row, "\t")),
    io:nl().

-define(IS_U8(X),  (X >= 0 andalso X =< 255)).
-define(IS_U16(X), (X >= 0 andalso X =< 65535)).

format_info_item(#resource{name = Name}) ->
    escape(Name);
format_info_item({N1, N2, N3, N4} = Value) when
      ?IS_U8(N1), ?IS_U8(N2), ?IS_U8(N3), ?IS_U8(N4) ->
    rabbit_misc:ntoa(Value);
format_info_item({K1, K2, K3, K4, K5, K6, K7, K8} = Value) when
      ?IS_U16(K1), ?IS_U16(K2), ?IS_U16(K3), ?IS_U16(K4),
      ?IS_U16(K5), ?IS_U16(K6), ?IS_U16(K7), ?IS_U16(K8) ->
    rabbit_misc:ntoa(Value);
format_info_item(Value) when is_pid(Value) ->
    rabbit_misc:pid_to_string(Value);
format_info_item(Value) when is_binary(Value) ->
    escape(Value);
format_info_item(Value) when is_atom(Value) ->
    escape(atom_to_list(Value));
format_info_item([{TableEntryKey, TableEntryType, _TableEntryValue} | _] =
                     Value) when is_binary(TableEntryKey) andalso
                                 is_atom(TableEntryType) ->
    io_lib:format("~1000000000000p", [prettify_amqp_table(Value)]);
format_info_item([T | _] = Value)
  when is_tuple(T) orelse is_pid(T) orelse is_binary(T) orelse is_atom(T) orelse
       is_list(T) ->
    "[" ++
        lists:nthtail(2, lists:append(
                           [", " ++ format_info_item(E) || E <- Value])) ++ "]";
format_info_item(Value) ->
    io_lib:format("~w", [Value]).

display_list(L) when is_list(L) ->
    lists:foreach(fun (I) when is_binary(I) ->
                          io:format("~s~n", [escape(I)]);
                      (I) when is_tuple(I) ->
                          display_row([escape(V)
                                       || V <- tuple_to_list(I)])
                  end,
                  lists:sort(L)),
    ok;
display_list(Other) -> Other.

display_call_result(Node, MFA) ->
    case call(Node, MFA) of
        {badrpc, _} = Res -> throw(Res);
        Res               -> io:format("~p~n", [Res]),
                             ok
    end.

unsafe_rpc(Node, Mod, Fun, Args) ->
    case rpc_call(Node, Mod, Fun, Args) of
        {badrpc, _} = Res -> throw(Res);
        Normal            -> Normal
    end.

call(Node, {Mod, Fun, Args}) ->
    rpc_call(Node, Mod, Fun, lists:map(fun list_to_binary/1, Args)).

rpc_call(Node, Mod, Fun, Args) ->
    rpc:call(Node, Mod, Fun, Args, ?RPC_TIMEOUT).

%% escape does C-style backslash escaping of non-printable ASCII
%% characters.  We don't escape characters above 127, since they may
%% form part of UTF-8 strings.

escape(Atom) when is_atom(Atom)  -> escape(atom_to_list(Atom));
escape(Bin)  when is_binary(Bin) -> escape(binary_to_list(Bin));
escape(L)    when is_list(L)     -> escape_char(lists:reverse(L), []).

escape_char([$\\ | T], Acc) ->
    escape_char(T, [$\\, $\\ | Acc]);
escape_char([X | T], Acc) when X >= 32, X /= 127 ->
    escape_char(T, [X | Acc]);
escape_char([X | T], Acc) ->
    escape_char(T, [$\\, $0 + (X bsr 6), $0 + (X band 8#070 bsr 3),
                    $0 + (X band 7) | Acc]);
escape_char([], Acc) ->
    Acc.

prettify_amqp_table(Table) ->
    [{escape(K), prettify_typed_amqp_value(T, V)} || {K, T, V} <- Table].

prettify_typed_amqp_value(longstr, Value) -> escape(Value);
prettify_typed_amqp_value(table,   Value) -> prettify_amqp_table(Value);
prettify_typed_amqp_value(array,   Value) -> [prettify_typed_amqp_value(T, V) ||
                                                 {T, V} <- Value];
prettify_typed_amqp_value(_Type,   Value) -> Value.

%% the slower shutdown on windows required to flush stdout
quit(Status) ->
    case os:type() of
        {unix,  _} -> halt(Status);
        {win32, _} -> init:stop(Status)
    end.
