﻿using System;
using System.Data.Linq.Mapping;

namespace BLToolkit.Reflection.MetadataProvider
{
	using Mapping;
	using Extension;

	public class LinqMetadataProvider : MetadataProviderBase
	{
		#region Helpers

		private Type  _type;
		private bool? _isLinqObject;

		private void EnsureMapper(Type type)
		{
			if (_type != type)
			{
				_type         = type;
				_isLinqObject = null;
			}
		}

		private bool IsLinqObject(Type type)
		{
			EnsureMapper(type);

			if (_isLinqObject == null)
			{
				var attrs = type.GetCustomAttributes(typeof(TableAttribute), true);
				_isLinqObject = attrs.Length > 0;
			}

			return _isLinqObject.Value;
		}

		#endregion

		#region GetFieldName

		public override string GetFieldName(TypeExtension typeExtension, MemberAccessor member, out bool isSet)
		{
			if (IsLinqObject(member.TypeAccessor.Type))
			{
				var a = member.GetAttribute<ColumnAttribute>();

				if (a != null && !string.IsNullOrEmpty(a.Name))
				{
					isSet = true;
					return a.Name;
				}
			}

			return base.GetFieldName(typeExtension, member, out isSet);
		}

		#endregion

		#region GetFieldStorage

		public override string GetFieldStorage(TypeExtension typeExtension, MemberAccessor member, out bool isSet)
		{
			if (IsLinqObject(member.TypeAccessor.Type))
			{
				var a = member.GetAttribute<ColumnAttribute>();

				if (a != null && !string.IsNullOrEmpty(a.Name))
				{
					isSet = true;
					return a.Storage;
				}
			}

			return base.GetFieldStorage(typeExtension, member, out isSet);
		}

		#endregion

		#region GetMapIgnore

		public override bool GetMapIgnore(TypeExtension typeExtension, MemberAccessor member, out bool isSet)
		{
			if (member.GetAttribute<AssociationAttribute>() != null)
			{
				isSet = true;
				return true;
			}

			if (IsLinqObject(member.TypeAccessor.Type))
			{
				isSet = true;
				return member.GetAttribute<ColumnAttribute>() == null;
			}

			return base.GetMapIgnore(typeExtension, member, out isSet);
		}

		#endregion

		#region GetNullable

		public override bool GetNullable(MappingSchema mappingSchema, TypeExtension typeExtension, MemberAccessor member, out bool isSet)
		{
			if (IsLinqObject(member.TypeAccessor.Type))
			{
				var attr = member.GetAttribute<ColumnAttribute>();

				if (attr != null)
				{
					isSet = true;
					return attr.CanBeNull;
				}
			}

			return base.GetNullable(mappingSchema, typeExtension, member, out isSet);
		}

		#endregion

		#region GetTableName

		public override string GetTableName(Type type, ExtensionList extensions, out bool isSet)
		{
			if (IsLinqObject(type))
			{
				isSet = true;

				var attrs = type.GetCustomAttributes(typeof(TableAttribute), true);

				return ((TableAttribute)attrs[0]).Name;
			}

			return base.GetTableName(type, extensions, out isSet);
		}

		#endregion

		#region GetPrimaryKeyOrder

		public override int GetPrimaryKeyOrder(Type type, TypeExtension typeExt, MemberAccessor member, out bool isSet)
		{
			if (IsLinqObject(type))
			{
				ColumnAttribute a = member.GetAttribute<ColumnAttribute>();

				if (a != null && a.IsPrimaryKey)
				{
					isSet = true;
					return 0;
				}
			}

			return base.GetPrimaryKeyOrder(type, typeExt, member, out isSet);
		}

		#endregion

		#region GetNonUpdatableFlag

		public override bool GetNonUpdatableFlag(Type type, TypeExtension typeExt, MemberAccessor member, out bool isSet)
		{
			if (IsLinqObject(member.TypeAccessor.Type))
			{
				var a = member.GetAttribute<ColumnAttribute>();

				if (a != null)
				{
					isSet = true;
					return a.IsDbGenerated;
				}
			}

			return base.GetNonUpdatableFlag(type, typeExt, member, out isSet);
		}

		#endregion

		#region GetAssociation

		public override Association GetAssociation(TypeExtension typeExtension, MemberAccessor member)
		{
			if (IsLinqObject(member.TypeAccessor.Type))
			{
				var a = member.GetAttribute<System.Data.Linq.Mapping.AssociationAttribute>();

				if (a != null)
					return new Association(member, Association.ParseKeys(a.ThisKey), Association.ParseKeys(a.OtherKey), a.Storage, true);
			}

			return base.GetAssociation(typeExtension, member);
		}

		#endregion

	}
}
