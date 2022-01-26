using SingleStoreConnector.Logging;
using SingleStoreConnector.Protocol;
using SingleStoreConnector.Protocol.Serialization;
using SingleStoreConnector.Utilities;

namespace SingleStoreConnector.Core;

internal sealed class SingleCommandPayloadCreator : ICommandPayloadCreator
{
	public static ICommandPayloadCreator Instance { get; } = new SingleCommandPayloadCreator();

	// This is chosen to be something very unlikely to appear as a column name in a user's query. If a result set is read
	// with this as the first column name, the result set will be treated as 'out' parameters for the previous command.
	public static string OutParameterSentinelColumnName => "\uE001\b\x0B";

	public bool WriteQueryCommand(ref CommandListPosition commandListPosition, IDictionary<string, CachedProcedure?> cachedProcedures, ByteBufferWriter writer)
	{
		if (commandListPosition.CommandIndex == commandListPosition.Commands.Count)
			return false;

		var command = commandListPosition.Commands[commandListPosition.CommandIndex];
		var preparedStatements = command.TryGetPreparedStatements();
		if (preparedStatements is null)
		{
			if (Log.IsTraceEnabled())
				Log.Trace("Session{0} Preparing command payload; CommandText: {1}", command.Connection!.Session.Id, command.CommandText);

			writer.Write((byte) CommandKind.Query);
			var supportsQueryAttributes = command.Connection!.Session.SupportsQueryAttributes;
			if (supportsQueryAttributes)
			{
				// attribute count
				var attributes = command.RawAttributes;
				writer.WriteLengthEncodedInteger((uint) (attributes?.Count ?? 0));

				// attribute set count (always 1)
				writer.Write((byte) 1);

				if (attributes?.Count > 0)
					WriteBinaryParameters(writer, attributes.Select(x => x.ToParameter()).ToArray(), command, true, 0);
			}
			else if (command.RawAttributes?.Count > 0)
			{
				Log.Warn("Session{0} has query attributes but server doesn't support them; CommandText: {1}", command.Connection!.Session.Id, command.CommandText);
			}

			WriteQueryPayload(command, cachedProcedures, writer);

			commandListPosition.CommandIndex++;
		}
		else
		{
			writer.Write((byte) CommandKind.StatementExecute);
			WritePreparedStatement(command, preparedStatements.Statements[commandListPosition.PreparedStatementIndex], writer);

			// advance to next prepared statement or next command
			if (++commandListPosition.PreparedStatementIndex == preparedStatements.Statements.Count)
			{
				commandListPosition.CommandIndex++;
				commandListPosition.PreparedStatementIndex = 0;
			}
		}
		return true;
	}

	/// <summary>
	/// Writes the text of <paramref name="command"/> to <paramref name="writer"/>, encoded in UTF-8.
	/// </summary>
	/// <param name="command">The command.</param>
	/// <param name="cachedProcedures">The cached procedures.</param>
	/// <param name="writer">The output writer.</param>
	/// <returns><c>true</c> if a complete command was written; otherwise, <c>false</c>.</returns>
	public static bool WriteQueryPayload(IMySqlCommand command, IDictionary<string, CachedProcedure?> cachedProcedures, ByteBufferWriter writer) =>
		(command.CommandType == CommandType.StoredProcedure) ? WriteStoredProcedure(command, cachedProcedures, writer) : WriteCommand(command, writer);

	private static void WritePreparedStatement(IMySqlCommand command, PreparedStatement preparedStatement, ByteBufferWriter writer)
	{
		var parameterCollection = command.RawParameters;

		if (Log.IsTraceEnabled())
			Log.Trace("Session{0} Preparing command payload; CommandId: {1}; CommandText: {2}", command.Connection!.Session.Id, preparedStatement.StatementId, command.CommandText);

		var attributes = command.RawAttributes;
		var supportsQueryAttributes = command.Connection!.Session.SupportsQueryAttributes;
		writer.Write(preparedStatement.StatementId);

		// NOTE: documentation is not updated yet, but due to bugs in MySQL Server 8.0.23-8.0.25, the PARAMETER_COUNT_AVAILABLE (0x08)
		// flag has to be set in the 'flags' block in order for query attributes to be sent with a prepared statement.
		var sendQueryAttributes = supportsQueryAttributes && command.Connection.Session.ServerVersion.Version is not { Major: 8, Minor: 0, Build: >= 23 and <= 25 };
		writer.Write((byte) (sendQueryAttributes ? 8 : 0));
		writer.Write(1);

		var commandParameterCount = preparedStatement.Statement.ParameterNames?.Count ?? 0;
		var attributeCount = attributes?.Count ?? 0;
		if (sendQueryAttributes)
		{
			writer.WriteLengthEncodedInteger((uint) (commandParameterCount + attributeCount));
		}
		else
		{
			if (supportsQueryAttributes && commandParameterCount > 0)
				writer.WriteLengthEncodedInteger((uint) commandParameterCount);
			if (attributeCount > 0)
			{
				Log.Warn("Session{0} has attributes for CommandId {1} but the server does not support them", command.Connection!.Session.Id, preparedStatement.StatementId);
				attributeCount = 0;
			}
		}

		if (commandParameterCount > 0 || attributeCount > 0)
		{
			// TODO: How to handle incorrect number of parameters?

			// build subset of parameters for this statement
			var parameters = new SingleStoreParameter[commandParameterCount + attributeCount];
			for (var i = 0; i < commandParameterCount; i++)
			{
				var parameterName = preparedStatement.Statement.ParameterNames![i];
				var parameterIndex = parameterName is not null ? (parameterCollection?.NormalizedIndexOf(parameterName) ?? -1) : preparedStatement.Statement.ParameterIndexes[i];
				if (parameterIndex == -1 && parameterName is not null)
					throw new SingleStoreException("Parameter '{0}' must be defined.".FormatInvariant(parameterName));
				else if (parameterIndex < 0 || parameterIndex >= (parameterCollection?.Count ?? 0))
					throw new SingleStoreException("Parameter index {0} is invalid when only {1} parameter{2} defined.".FormatInvariant(parameterIndex, parameterCollection?.Count ?? 0, parameterCollection?.Count == 1 ? " is" : "s are"));
				parameters[i] = parameterCollection![parameterIndex];
			}
			for (var i = 0; i < attributeCount; i++)
				parameters[commandParameterCount + i] = attributes![i].ToParameter();
			WriteBinaryParameters(writer, parameters, command, supportsQueryAttributes, commandParameterCount);
		}
	}

	private static void WriteBinaryParameters(ByteBufferWriter writer, SingleStoreParameter[] parameters, IMySqlCommand command, bool supportsQueryAttributes, int parameterCount)
	{
		// write null bitmap
		byte nullBitmap = 0;
		for (var i = 0; i < parameters.Length; i++)
		{
			var parameter = parameters[i];
			if (parameter.Value is null || parameter.Value == DBNull.Value)
				nullBitmap |= (byte) (1 << (i % 8));

			if (i % 8 == 7)
			{
				writer.Write(nullBitmap);
				nullBitmap = 0;
			}
		}
		if (parameters.Length % 8 != 0)
			writer.Write(nullBitmap);

		// write "new parameters bound" flag
		writer.Write((byte) 1);

		for (var index = 0; index < parameters.Length; index++)
		{
			// override explicit SingleStoreDbType with inferred type from the Value
			var parameter = parameters[index];
			var mySqlDbType = parameter.SingleStoreDbType;
			var typeMapping = (parameter.Value is null || parameter.Value == DBNull.Value) ? null : TypeMapper.Instance.GetDbTypeMapping(parameter.Value.GetType());
			if (typeMapping is not null)
			{
				var dbType = typeMapping.DbTypes[0];
				mySqlDbType = TypeMapper.Instance.GetSingleStoreDbTypeForDbType(dbType);
			}

			writer.Write(TypeMapper.ConvertToColumnTypeAndFlags(mySqlDbType, command.Connection!.GuidFormat));

			if (supportsQueryAttributes)
			{
				if (index < parameterCount)
					writer.Write((byte) 0); // empty string
				else
					writer.WriteLengthEncodedString(parameter.ParameterName);
			}
		}

		var options = command.CreateStatementPreparerOptions();
		foreach (var parameter in parameters)
			parameter.AppendBinary(writer, options);
	}

	private static bool WriteStoredProcedure(IMySqlCommand command, IDictionary<string, CachedProcedure?> cachedProcedures, ByteBufferWriter writer)
	{
		var parameterCollection = command.RawParameters;
		var cachedProcedure = cachedProcedures[command.CommandText!];
		if (cachedProcedure is not null)
			parameterCollection = cachedProcedure.AlignParamsWithDb(parameterCollection);

		SingleStoreParameter? returnParameter = null;
		var outParameters = new SingleStoreParameterCollection();
		var outParameterNames = new List<string>();
		var inParameters = new SingleStoreParameterCollection();
		var argParameterNames = new List<string>();
		var inOutSetParameters = "";
		for (var i = 0; i < (parameterCollection?.Count ?? 0); i++)
		{
			var param = parameterCollection![i];
			var inName = "@inParam" + i;
			var outName = "@outParam" + i;
			switch (param.Direction)
			{
			case ParameterDirection.Input:
			case ParameterDirection.InputOutput:
				var inParam = param.WithParameterName(inName);
				inParameters.Add(inParam);
				if (param.Direction == ParameterDirection.InputOutput)
				{
					inOutSetParameters += $"SET {outName}={inName}; ";
					goto case ParameterDirection.Output;
				}
				argParameterNames.Add(inName);
				break;
			case ParameterDirection.Output:
				outParameters.Add(param);
				outParameterNames.Add(outName);
				argParameterNames.Add(outName);
				break;
			case ParameterDirection.ReturnValue:
				returnParameter = param;
				break;
			}
		}

		// if a return param is set, assume it is a function; otherwise, assume stored procedure
		var commandText = command.CommandText + "(" + string.Join(", ", argParameterNames) + ");";
		if (returnParameter is null)
		{
			commandText = inOutSetParameters + "CALL " + commandText;
			if (outParameters.Count > 0 && (command.CommandBehavior & CommandBehavior.SchemaOnly) == 0)
			{
				commandText += "SELECT '" + OutParameterSentinelColumnName + "' AS '" + OutParameterSentinelColumnName + "', " + string.Join(", ", outParameterNames);
			}
		}
		else
		{
			commandText = "SELECT " + commandText;
		}
		command.OutParameters = outParameters;
		command.ReturnParameter = returnParameter;

		var preparer = new StatementPreparer(commandText, inParameters, command.CreateStatementPreparerOptions());
		return preparer.ParseAndBindParameters(writer);
	}

	private static bool WriteCommand(IMySqlCommand command, ByteBufferWriter writer)
	{
		var isSchemaOnly = (command.CommandBehavior & CommandBehavior.SchemaOnly) != 0;
		var isSingleRow = (command.CommandBehavior & CommandBehavior.SingleRow) != 0;
		if (isSchemaOnly)
		{
			ReadOnlySpan<byte> setSqlSelectLimit0 = new byte[] { 83, 69, 84, 32, 115, 113, 108, 95, 115, 101, 108, 101, 99, 116, 95, 108, 105, 109, 105, 116, 61, 48, 59, 10 }; // SET sql_select_limit=0;\n
			writer.Write(setSqlSelectLimit0);
		}
		else if (isSingleRow)
		{
			ReadOnlySpan<byte> setSqlSelectLimit1 = new byte[] { 83, 69, 84, 32, 115, 113, 108, 95, 115, 101, 108, 101, 99, 116, 95, 108, 105, 109, 105, 116, 61, 49, 59, 10 }; // SET sql_select_limit=1;\n
			writer.Write(setSqlSelectLimit1);
		}
		var preparer = new StatementPreparer(command.CommandText!, command.RawParameters, command.CreateStatementPreparerOptions());
		var isComplete = preparer.ParseAndBindParameters(writer);
		if (isComplete && (isSchemaOnly || isSingleRow))
		{
			ReadOnlySpan<byte> clearSqlSelectLimit = new byte[] { 10, 83, 69, 84, 32, 115, 113, 108, 95, 115, 101, 108, 101, 99, 116, 95, 108, 105, 109, 105, 116, 61, 100, 101, 102, 97, 117, 108, 116, 59 }; // \nSET sql_select_limit=default;
			writer.Write(clearSqlSelectLimit);
		}
		return isComplete;
	}

	static readonly ISingleStoreConnectorLogger Log = SingleStoreConnectorLogManager.CreateLogger(nameof(SingleCommandPayloadCreator));
}