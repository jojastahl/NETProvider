/*
 *	Firebird ADO.NET Data provider for .NET and Mono
 *
 *	   The contents of this file are subject to the Initial
 *	   Developer's Public License Version 1.0 (the "License");
 *	   you may not use this file except in compliance with the
 *	   License. You may obtain a copy of the License at
 *	   http://www.firebirdsql.org/index.php?op=doc&id=idpl
 *
 *	   Software distributed under the License is distributed on
 *	   an "AS IS" basis, WITHOUT WARRANTY OF ANY KIND, either
 *	   express or implied. See the License for the specific
 *	   language governing rights and limitations under the License.
 *
 *	Copyright (c) 2002, 2007 Carlos Guzman Alvarez
 *	All Rights Reserved.
 *
 *  Contributors:
 *      Jiri Cincura (jiri@cincura.net)
 */

#define VARIANTE_3

using System;
using System.Collections.Generic;
using System.Data;
using System.Text;
using System.Diagnostics;
using System.IO;

using FirebirdSql.Data.Common;
using FirebirdSql.Data.Schema;


namespace FirebirdSql.Data.FirebirdClient
{
	internal class FbConnectionInternal : MarshalByRefObject, IDisposable
	{
		#region Fields

		private IDatabase _db;
		private FbTransaction _activeTransaction;
		private List<WeakReference> _preparedCommands;
#if VARIANTE_3
		private int _preparedCommandsUsed;
		private int _preparedCommandsAddCalled;
#endif
#if VARIANTE_2
		private int _preparedCommandsReused;
		private int _preparedCommandsAdded;
		private int _preparedCommandsRemoved;
		private int _preparedCommandsOptimal;
#endif
#if VARIANTE_1
		private WeakReference<Queue<WeakReference>> _wrefStock;
#endif
		private FbConnectionString _options;
		private FbConnection _owningConnection;
		private bool _disposed;
		private object _preparedCommandsCleanupSyncRoot;
		private FbEnlistmentNotification _enlistmentNotification;

		#endregion

		#region Properties

		public IDatabase Database
		{
			get { return _db; }
		}

		public bool HasActiveTransaction
		{
			get
			{
				return _activeTransaction != null && !_activeTransaction.IsUpdated;
			}
		}

		public FbTransaction ActiveTransaction
		{
			get { return _activeTransaction; }
		}

		public FbConnection OwningConnection
		{
			get { return _owningConnection; }
		}

		public bool IsEnlisted
		{
			get { return _enlistmentNotification != null && !_enlistmentNotification.IsCompleted; }
		}

		public FbConnectionString Options
		{
			get { return _options; }
		}

		public bool CancelDisabled { get; set; }

		#endregion

		#region Constructors

		public FbConnectionInternal(FbConnectionString options)
		{
			_preparedCommands = new List<WeakReference>();
			_preparedCommandsCleanupSyncRoot = new object();
#if VARIANTE_2
			new PreparedCommnandOptimisationHelper(this);
#endif

			_options = options;

			GC.SuppressFinalize(this);
		}

		#endregion

		#region Finalizer

		~FbConnectionInternal()
		{
			// Do not re-create Dispose clean-up code here.
			// Calling Dispose(false) is optimal in terms of
			// readability and maintainability.
			Dispose(false);
		}

		#endregion

		#region IDisposable Methods

		public void Dispose()
		{
			Dispose(true);

			// This object will be cleaned up by the Dispose method.
			// Therefore, you should call GC.SupressFinalize to
			// take this object off the finalization queue
			// and prevent finalization code for this object
			// from executing a second time.
			GC.SuppressFinalize(this);
		}

		protected void Dispose(bool disposing)
		{
			lock (this)
			{
				if (!_disposed)
				{
					// release any unmanaged resources
					Disconnect();

					if (disposing)
					{
#if VARIANTE_1
						_wrefStock = null;
#endif
					}

					_disposed = true;
				}
			}
		}

		#endregion

		#region Create and Drop database methods

		public void CreateDatabase(DatabaseParameterBuffer dpb)
		{
			IDatabase db = ClientFactory.CreateDatabase(_options);
			db.CreateDatabase(dpb, _options.DataSource, _options.Port, _options.Database);
		}

		public void DropDatabase()
		{
			IDatabase db = ClientFactory.CreateDatabase(_options);
			db.Attach(BuildDpb(db, _options), _options.DataSource, _options.Port, _options.Database);
			db.DropDatabase();
		}

		#endregion

		#region Connect and Disconnect methods

		public void Connect()
		{
			if (Charset.GetCharset(_options.Charset) == null)
			{
				throw new FbException("Invalid character set specified");
			}

			try
			{
				_db = ClientFactory.CreateDatabase(_options);
				_db.Charset = Charset.GetCharset(_options.Charset);
				_db.Dialect = _options.Dialect;
				_db.PacketSize = _options.PacketSize;

				DatabaseParameterBuffer dpb = BuildDpb(_db, _options);

				if (_options.FallIntoTrustedAuth)
				{
					_db.AttachWithTrustedAuth(dpb, _options.DataSource, _options.Port, _options.Database);
				}
				else
				{
					_db.Attach(dpb, _options.DataSource, _options.Port, _options.Database);
				}
			}
			catch (IscException ex)
			{
				throw new FbException(ex.Message, ex);
			}
		}

		public void Disconnect()
		{
			if (_db != null)
			{
				try
				{
					_db.Dispose();
				}
				catch
				{
				}
				finally
				{
					_db = null;
					_owningConnection = null;
					_options = null;
				}
			}
		}

		#endregion

		#region Transaction Handling Methods

		public FbTransaction BeginTransaction(IsolationLevel level, string transactionName)
		{
			lock (this)
			{
				if (HasActiveTransaction)
				{
					throw new InvalidOperationException("A transaction is currently active. Parallel transactions are not supported.");
				}

				try
				{
					_activeTransaction = new FbTransaction(_owningConnection, level);
					_activeTransaction.BeginTransaction();

					if (transactionName != null)
					{
						_activeTransaction.Save(transactionName);
					}
				}
				catch (IscException ex)
				{
					throw new FbException(ex.Message, ex);
				}
			}

			return _activeTransaction;
		}

		public FbTransaction BeginTransaction(FbTransactionOptions options, string transactionName)
		{
			lock (this)
			{
				if (HasActiveTransaction)
				{
					throw new InvalidOperationException("A transaction is currently active. Parallel transactions are not supported.");
				}

				try
				{
					_activeTransaction = new FbTransaction(
						_owningConnection, IsolationLevel.Unspecified);

					_activeTransaction.BeginTransaction(options);

					if (transactionName != null)
					{
						_activeTransaction.Save(transactionName);
					}
				}
				catch (IscException ex)
				{
					throw new FbException(ex.Message, ex);
				}
			}

			return _activeTransaction;
		}

		public void DisposeTransaction()
		{
			if (_activeTransaction != null && !IsEnlisted)
			{
				_activeTransaction.Dispose();
				_activeTransaction = null;
			}
		}

		public void TransactionUpdated()
		{
			for (int i = 0; i < _preparedCommands.Count; i++)
			{
				FbCommand command;
				if (!_preparedCommands[i].TryGetTarget<FbCommand>(out command))
					continue;

				if (command.Transaction != null)
				{
					command.CloseReader();
					command.Transaction = null;
				}
			}
		}

		#endregion

		#region Transaction Enlistement

		public void EnlistTransaction(System.Transactions.Transaction transaction)
		{
			if (_owningConnection != null && _options.Enlist)
			{
				if (_enlistmentNotification != null && _enlistmentNotification.SystemTransaction == transaction)
					return;

				if (HasActiveTransaction)
				{
					throw new ArgumentException("Unable to enlist in transaction, a local transaction already exists");
				}
				if (_enlistmentNotification != null)
				{
					throw new ArgumentException("Already enlisted in a transaction");
				}

				_enlistmentNotification = new FbEnlistmentNotification(this, transaction);
				_enlistmentNotification.Completed += new EventHandler(EnlistmentCompleted);
			}
		}

		private void EnlistmentCompleted(object sender, EventArgs e)
		{
			_enlistmentNotification = null;
		}

		public FbTransaction BeginTransaction(System.Transactions.IsolationLevel isolationLevel)
		{
			switch (isolationLevel)
			{
				case System.Transactions.IsolationLevel.Chaos:
					return BeginTransaction(System.Data.IsolationLevel.Chaos, null);

				case System.Transactions.IsolationLevel.ReadUncommitted:
					return BeginTransaction(System.Data.IsolationLevel.ReadUncommitted, null);

				case System.Transactions.IsolationLevel.RepeatableRead:
					return BeginTransaction(System.Data.IsolationLevel.RepeatableRead, null);

				case System.Transactions.IsolationLevel.Serializable:
					return BeginTransaction(System.Data.IsolationLevel.Serializable, null);

				case System.Transactions.IsolationLevel.Snapshot:
					return BeginTransaction(System.Data.IsolationLevel.Snapshot, null);

				case System.Transactions.IsolationLevel.Unspecified:
					return BeginTransaction(System.Data.IsolationLevel.Unspecified, null);

				case System.Transactions.IsolationLevel.ReadCommitted:
				default:
					return BeginTransaction(System.Data.IsolationLevel.ReadCommitted, null);
			}
		}

		#endregion

		#region Schema Methods

		public DataTable GetSchema(string collectionName, string[] restrictions)
		{
			return FbSchemaFactory.GetSchema(_owningConnection, collectionName, restrictions);
		}

		#endregion

		#region Prepared Commands Methods

#if VARIANTE_3
		public void AddPreparedCommand(FbCommand command)
		{
			lock (_preparedCommandsCleanupSyncRoot)
			{
				int position = -1;
				bool found = false;
				int used = 0;
				for (int i = 0; i < _preparedCommands.Count; i++)
				{
					FbCommand current;
					if (!_preparedCommands[i].TryGetTarget<FbCommand>(out current))
					{
						position = i;
					}
					else
					{
						used++;
						if (current == command)
							found = true;
					}
				}
				if (!found)
					used++;
				if (used > _preparedCommandsUsed)
					_preparedCommandsUsed = used;
				if (!found)
				{
					_preparedCommandsAddCalled++;
					if (position >= 0)
						_preparedCommands[position].Target = command;
					else
						_preparedCommands.Add(new WeakReference(command));
#if DEBUG
					Debug.WriteLine("Count = {0}, _preparedCommandsUsed = {1}, used = {2}, adds = {3}", _preparedCommands.Count, _preparedCommandsUsed, used, _preparedCommandsAddCalled);
#endif
					if (_preparedCommandsAddCalled >= Math.Max(8, Math.Min(128, _preparedCommands.Count)))
					{
						if (_preparedCommandsUsed + Math.Max(1, _preparedCommandsUsed / 8) < _preparedCommands.Count)
						{
							int j = 0;
							for (int i = _preparedCommands.Count - 1; i >= _preparedCommandsUsed; --i)
							{
								var tmp = _preparedCommands[i].Target;
								if (tmp != null)
								{
									while (j < i && _preparedCommands[j].Target != null)
										j++;
									Debug.Assert(j < i);
									if (j < i)
									{
										_preparedCommands[j].Target = tmp;
										j++;
									}
								}
							}
#if DEBUG
							Debug.WriteLine("Count = {0}, NewCount = {1}", _preparedCommands.Count, _preparedCommandsUsed);
#endif
							_preparedCommands.RemoveRange(_preparedCommandsUsed, _preparedCommands.Count - _preparedCommandsUsed);
						}
						_preparedCommandsAddCalled = 0;
						_preparedCommandsUsed = 0;
					}
				}
			}
		}

		public void RemovePreparedCommand(FbCommand command)
		{
			lock (_preparedCommandsCleanupSyncRoot)
			{
				for (int i = _preparedCommands.Count - 1; i >= 0; i--)
				{
					var item = _preparedCommands[i];
					FbCommand current;
					if (item.TryGetTarget(out current) && current == command)
					{
						item.Target = null;
						return;
					}
				}
			}
		}

		public void ReleasePreparedCommands()
		{
			lock (_preparedCommandsCleanupSyncRoot)
			{
				WeakReference[] toProcess = new WeakReference[_preparedCommands.Count];
				_preparedCommands.CopyTo(toProcess);
				for (int i = 0; i < toProcess.Length; i++)
				{
					FbCommand current;
					if (!toProcess[i].TryGetTarget(out current))
						continue;

					try
					{
						// Release statement handle
						current.Release();
					}
					catch (System.IO.IOException)
					{
						// If an IO error occurs weh trying to release the command
						// avoid it. ( It maybe the connection to the server was down
						// for unknown reasons. )
					}
					catch (IscException ex)
					{
						if (ex.ErrorCode != IscCodes.isc_net_read_err &&
							ex.ErrorCode != IscCodes.isc_net_write_err &&
							ex.ErrorCode != IscCodes.isc_network_error)
						{
							throw;
						}
					}
				}
				_preparedCommands.Clear();
				_preparedCommandsUsed = 0;
				_preparedCommandsAddCalled = 0;
			}
		}

#endif

#if VARIANTE_2
		public void AddPreparedCommand(FbCommand command)
		{
			lock (_preparedCommandsCleanupSyncRoot)
			{
				int position = -1;
				for (int i = 0; i < _preparedCommands.Count; i++)
				{
					FbCommand current;
					if (!_preparedCommands[i].TryGetTarget<FbCommand>(out current))
					{
						if (position == -1)
							position = i;
					}
					else
					{
						if (current == command)
						{
							return;
						}
					}
				}
				if (position >= 0)
				{
					_preparedCommands[position].Target = command;
					_preparedCommandsReused++;
				}
				else
				{
					_preparedCommands.Add(new WeakReference(command));
					_preparedCommandsAdded++;
				}
			}
		}

		private class PreparedCommnandOptimisationHelper
		{
			private FbConnectionInternal _connection;
			private int _lastGuess;
			public PreparedCommnandOptimisationHelper(FbConnectionInternal connection, int lastGuess = 0)
			{
				_connection = connection;
				_lastGuess = lastGuess;
			}
			~PreparedCommnandOptimisationHelper()
			{
				lock (_connection._preparedCommandsCleanupSyncRoot)
				{
					int guess = _connection._preparedCommandsAdded + Math.Max(0, _connection._preparedCommandsReused - _connection._preparedCommandsRemoved);
					if (_connection._preparedCommandsAdded != 0 && guess - _lastGuess < _connection._preparedCommandsAdded)
						guess = _lastGuess + _connection._preparedCommandsAdded;
					_connection._preparedCommandsOptimal = Math.Max(0, _connection._preparedCommandsOptimal + guess - _lastGuess);
					Debug.Print("CurrentCount = {0}, Optimal = {1}, Added = {2}, Reused = {3}, Removed = {4}, Guess = {5}, LastGuess = {6}", _connection._preparedCommands.Count, _connection._preparedCommandsOptimal, _connection._preparedCommandsAdded, _connection._preparedCommandsReused, _connection._preparedCommandsRemoved, guess, _lastGuess);
					_connection._preparedCommandsAdded = 0;
					_connection._preparedCommandsReused = 0;
					_connection._preparedCommandsRemoved = 0;
					new PreparedCommnandOptimisationHelper(_connection, guess);
				}
			}
		}

		public void RemovePreparedCommand(FbCommand command)
		{
			lock (_preparedCommandsCleanupSyncRoot)
			{
				if (_preparedCommandsReused == 0 && _preparedCommandsAdded == 0)
				{
					// Calling RemovePreparedCommand multiple times without AddPreparedCommand,
					// Keep all WeakReferences and scan just for the command to remove
					for (int i = 0; i < _preparedCommands.Count; ++i)
					{
						var item = _preparedCommands[i];
						FbCommand current;
						if (item.TryGetTarget(out current))
						{
							if (current == command)
							{
								item.Target = null;
								_preparedCommandsRemoved++;
								return;
							}
						}
					}
				}
				else
				{
					// Some commands added since last call to RemovePreparedCommand,
					// We take the number of adds as an indicator how many WeakReferences
					// we should keep around for future adds
					// While scanning for command to remove, concentrate all used
					// WeakReferences at the beginning of the list
					int k = 0;
					for (int i = 0; i < _preparedCommands.Count; ++i)
					{
						var item = _preparedCommands[i];
						FbCommand current;
						if (item.TryGetTarget(out current))
						{
							if (current == command)
							{
								item.Target = null;
								_preparedCommandsRemoved++;
							}
							else
							{
								_preparedCommands[i] = _preparedCommands[k];
								_preparedCommands[k++] = item;
							}
						}
					}
					if (k <= _preparedCommandsOptimal && _preparedCommandsOptimal + Math.Max(1, _preparedCommandsOptimal / 8) < _preparedCommands.Count)
						_preparedCommands.RemoveRange(_preparedCommandsOptimal, _preparedCommands.Count - _preparedCommandsOptimal);
				}
			}
		}

		public void ReleasePreparedCommands()
		{

			lock (_preparedCommandsCleanupSyncRoot)
			{
				WeakReference[] toProcess = new WeakReference[_preparedCommands.Count];
				_preparedCommands.CopyTo(toProcess);
				for (int i = 0; i < toProcess.Length; i++)
				{
					FbCommand current;
					if (!toProcess[i].TryGetTarget(out current))
						continue;

					try
					{
						// Release statement handle
						current.Release();
					}
					catch (System.IO.IOException)
					{
						// If an IO error occurs weh trying to release the command
						// avoid it. ( It maybe the connection to the server was down
						// for unknown reasons. )
					}
					catch (IscException ex)
					{
						if (ex.ErrorCode != IscCodes.isc_net_read_err &&
							ex.ErrorCode != IscCodes.isc_net_write_err &&
							ex.ErrorCode != IscCodes.isc_network_error)
						{
							throw;
						}
					}
				}
				_preparedCommands.Clear();
				_preparedCommandsAdded = 0;
			}
		}
#endif

#if VARIANTE_1
		public void AddPreparedCommand(FbCommand command)
		{
			lock (_preparedCommandsCleanupSyncRoot)
			{
				int position = -1;
				for (int i = 0; i < _preparedCommands.Count; i++)
				{
					FbCommand current;
					if (!_preparedCommands[i].TryGetTarget<FbCommand>(out current))
					{
						position = i;
					}
					else
					{
						if (current == command)
						{
							return;
						}
					}
				}
				if (position >= 0)
				{
					_preparedCommands[position].Target = command;
				}
				else
				{
					Queue<WeakReference> wrefQueue;
					var tmpStock = _wrefStock;
					if (tmpStock != null && tmpStock.TryGetTarget(out wrefQueue) && wrefQueue.Count != 0)
					{
						var wref = wrefQueue.Dequeue();
						wref.Target = command;
						_preparedCommands.Add(wref);
					}
					else
					{
						_preparedCommands.Add(new WeakReference(command));
					}
				}
			}
		}

		public void RemovePreparedCommand(FbCommand command)
		{
			lock (_preparedCommandsCleanupSyncRoot)
			{
				Queue<WeakReference> wrefQueue;
				var tmpStock = _wrefStock;
				if (tmpStock == null)
				{
					wrefQueue = new Queue<WeakReference>();
					_wrefStock = new WeakReference<Queue<WeakReference>>(wrefQueue);
				}
				else
				{
					if (!tmpStock.TryGetTarget(out wrefQueue))
					{
						wrefQueue = new Queue<WeakReference>();
						tmpStock.SetTarget(wrefQueue);
					}
				}
				int i = 0;
				for (int j = i; j < _preparedCommands.Count; ++j)
				{
					var item = _preparedCommands[j];
					FbCommand current;
					if (item.TryGetTarget(out current))
					{
						if (current == command)
						{
							item.Target = null;
							wrefQueue.Enqueue(item);
						}
						else
						{
							_preparedCommands[i++] = item;
						}
					}
					else
					{
						wrefQueue.Enqueue(item);
					}
				}
				_preparedCommands.RemoveRange(i, _preparedCommands.Count - i);
			}
		}

		public void ReleasePreparedCommands()
		{

			lock (_preparedCommandsCleanupSyncRoot)
			{
				WeakReference[] toProcess = new WeakReference[_preparedCommands.Count];
				_preparedCommands.CopyTo(toProcess);
				for (int i = 0; i < toProcess.Length; i++)
				{
					FbCommand current;
					if (!toProcess[i].TryGetTarget(out current))
						continue;

					try
					{
						// Release statement handle
						current.Release();
					}
					catch (System.IO.IOException)
					{
						// If an IO error occurs weh trying to release the command
						// avoid it. ( It maybe the connection to the server was down
						// for unknown reasons. )
					}
					catch (IscException ex)
					{
						if (ex.ErrorCode != IscCodes.isc_net_read_err &&
							ex.ErrorCode != IscCodes.isc_net_write_err &&
							ex.ErrorCode != IscCodes.isc_network_error)
						{
							throw;
						}
					}
				}
				_preparedCommands.Clear();
				_wrefStock = null;
			}
		}
#endif

		#endregion

		#region Firebird Events Methods

		public void CloseEventManager()
		{
			if (_db != null && _db.HasRemoteEventSupport)
			{
				lock (_db)
				{
					_db.CloseEventManager();
				}
			}
		}

		#endregion

		#region Connection Verification

		public bool Verify()
		{
			// Do not actually ask for any information
			byte[] items = new byte[]
			{
				IscCodes.isc_info_end
			};

			try
			{
				_db.GetDatabaseInfo(items, 16);

				return true;
			}
			catch
			{
				return false;
			}
		}

		#endregion

		#region Private Methods

		private DatabaseParameterBuffer BuildDpb(IDatabase db, FbConnectionString options)
		{
			DatabaseParameterBuffer dpb = new DatabaseParameterBuffer();

			dpb.Append(IscCodes.isc_dpb_version1);
			dpb.Append(IscCodes.isc_dpb_dummy_packet_interval, new byte[] { 120, 10, 0, 0 });
			dpb.Append(IscCodes.isc_dpb_sql_dialect, new byte[] { Convert.ToByte(options.Dialect), 0, 0, 0 });
			dpb.Append(IscCodes.isc_dpb_lc_ctype, options.Charset);
			if (options.DbCachePages > 0)
			{
				dpb.Append(IscCodes.isc_dpb_num_buffers, options.DbCachePages);
			}
			if (!string.IsNullOrEmpty(options.Role))
			{
				dpb.Append(IscCodes.isc_dpb_sql_role_name, options.Role);
			}
			dpb.Append(IscCodes.isc_dpb_connect_timeout, options.ConnectionTimeout);

			if (!options.FallIntoTrustedAuth)
			{
				dpb.Append(IscCodes.isc_dpb_user_name, options.UserID);
				dpb.Append(IscCodes.isc_dpb_password, options.Password);
			}
			dpb.Append(IscCodes.isc_dpb_process_id, GetProcessId());
			dpb.Append(IscCodes.isc_dpb_process_name, GetProcessName());
			if (options.NoDatabaseTriggers)
			{
				dpb.Append(IscCodes.isc_dpb_no_db_triggers, 1);
			}
			if (options.NoGarbageCollect)
			{
				dpb.Append(IscCodes.isc_dpb_no_garbage_collect, (byte)0);
			}

			return dpb;
		}

		private string GetProcessName()
		{
			// showing ApplicationPhysicalPath may be wrong because of connection pooling; better idea?
			return GetHostingPath() ?? GetRealProcessName();
		}


		private string GetHostingPath()
		{
			System.Reflection.Assembly assembly;
			try
			{
				assembly = System.Reflection.Assembly.Load(string.Format("System.Web, Version={0}.{1}.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", Environment.Version.Major, Environment.Version.Minor));
			}
			catch (FileNotFoundException)
			{
				return null;
			}
			catch (FileLoadException)
			{
				return null;
			}
			catch (BadImageFormatException)
			{
				return null;
			}
			return (string)assembly
				.GetType("System.Web.Hosting.HostingEnvironment")
				.GetProperty("ApplicationPhysicalPath")
				.GetValue(null, null);
		}
		private string GetRealProcessName()
		{
			System.Reflection.Assembly assembly = System.Reflection.Assembly.GetEntryAssembly();
			if (assembly != null)
			{
				return assembly.Location;
			}
			else // if we're not loaded from managed code
			{
				return Process.GetCurrentProcess().MainModule.FileName;
			}
		}

		private int GetProcessId()
		{
			System.Reflection.Assembly assembly = System.Reflection.Assembly.GetEntryAssembly();
			if (assembly != null)
			{
				if (assembly.IsFullyTrusted)
					return Process.GetCurrentProcess().Id;
				else
					return -1;
			}
			else // if we're not loaded from managed code
			{
				return Process.GetCurrentProcess().Id;
			}
		}
		#endregion

		#region Cancelation
		public void EnableCancel()
		{
			_db.CancelOperation(IscCodes.fb_cancel_enable);
			CancelDisabled = false;
		}

		public void DisableCancel()
		{
			_db.CancelOperation(IscCodes.fb_cancel_disable);
			CancelDisabled = true;
		}

		public void CancelCommand()
		{
			_db.CancelOperation(IscCodes.fb_cancel_raise);
		}
		#endregion

		#region Infrastructure
		public FbConnectionInternal SetOwningConnection(FbConnection owningConnection)
		{
			_owningConnection = owningConnection;
			return this;
		}
		#endregion
	}
}
