/*
 * File:    MapEmit.cs
 * Created: 07/17/2003
 * Author:  Igor Tkachev
 *          mailto:it@rsdn.ru
 */

using System;
using System.IO;
using System.Collections;
using System.Data;
using System.Threading;
using System.Reflection;
using System.Reflection.Emit;

namespace Rsdn.Framework.Data.Mapping
{
	internal class MapEmit
	{
		private MapEmit()
		{
		}

		private static bool IsMemberOfType(Type memberType, Type type)
		{
			return 
				type == memberType || 
				memberType.IsEnum && Enum.GetUnderlyingType(memberType) == type;
		}

		private static bool IsBasedOnType(Type memberType, Type type)
		{
			for (Type baseType = memberType.BaseType; baseType != null && baseType != typeof(object); baseType = baseType.BaseType)
				if (baseType == type)
					return true;

			return false;
		}

		private static MemberInfo GetValueMember(Type type, Type memberType)
		{
			FieldInfo[] fields = type.GetFields(
				BindingFlags.Public | BindingFlags.Instance | BindingFlags.GetField | BindingFlags.SetField);

			foreach (FieldInfo field in fields)
				if (field.Name == "Value" && IsMemberOfType(memberType, field.FieldType))
					return field;

			foreach (FieldInfo field in fields)
				if (IsMemberOfType(memberType, field.FieldType))
					return field;

			foreach (FieldInfo field in fields)
				if (field.Name == "Value" && IsBasedOnType(memberType, field.FieldType))
					return field;

			PropertyInfo[] props = type.GetProperties(
				BindingFlags.Public | BindingFlags.Instance | BindingFlags.GetProperty | BindingFlags.SetProperty);

			foreach (PropertyInfo prop in props)
				if (prop.Name == "Value" && IsMemberOfType(memberType, prop.PropertyType))
					return prop;

			foreach (PropertyInfo prop in props)
				if (IsMemberOfType(memberType, prop.PropertyType))
					return prop;

			foreach (PropertyInfo prop in props)
				if (prop.Name == "Value" && IsBasedOnType(memberType, prop.PropertyType))
					return prop;

			return null;
		}

		private static ConstructorInfo GetDefaultConstructor(Type type)
		{
			return type.GetConstructor(
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null,
				Type.EmptyTypes,
				null);
		}

		class GenContext
		{
			// Type context.
			//
			public Type         Type;        // type
			public TypeBuilder  TypeBuilder; // type builder
			public MapGenerator IniCtorGen;  // static constructor generator
			public MapGenerator DefCtorGen;  // default constructor generator
			public MapGenerator ObfCtorGen;  // object factory constructor generator
			public MapGenerator InitGen;     // init property method generator

			public ArrayList    Objects;     // created object's list

			// Property context.
			//
			public PropertyInfo PropertyInfo;    // property info
			public object []    AttributeParams; // property attribute parameters
			public FieldBuilder FieldBuilder;    // property implementation field builder
			public Type         FieldType;       // field type
			public bool         IsObject;        // true - field is a reference type
			public FieldBuilder ParamBuilder;    // parameter builder
			public MethodInfo   GetMethodInfo;   // getter info
			public MethodInfo   SetMethodInfo;   // setter info

			public LocalBuilder InitDataField;   // MapInitializingData local variable
		}

		private static Type[]    _factoryParams             = new Type[] { typeof(MapInitializingData) };
		private static Hashtable _defaultConstructorBuilder = new Hashtable();
		private static Hashtable _factoryConstructorBuilder = new Hashtable();

		private static Type CreateNonAbstractType(Type type, ModuleBuilder moduleBuilder)
		{
			GenContext ctx = new GenContext();

			ctx.Type    = type;
			ctx.Objects = new ArrayList();

			ctx.TypeBuilder = moduleBuilder.DefineType(
				type.FullName.Replace('+', '.') + ".MappingExtension." + type.Name,
				TypeAttributes.Public | TypeAttributes.BeforeFieldInit,
				type,
				new Type[] { typeof(IMapGenerated) } );

			// Set Serializable attribute.
			//
			if (type.IsSerializable)
			{
				ConstructorInfo        ci        = typeof(SerializableAttribute).GetConstructor(Type.EmptyTypes);
				CustomAttributeBuilder caBuilder = new CustomAttributeBuilder(ci, new object[0]);

				ctx.TypeBuilder.SetCustomAttribute(caBuilder);
			}

			// Create initializer for the type.
			//
			ConstructorBuilder initCtorBuilder = ctx.TypeBuilder.DefineTypeInitializer();

			// Create default constructor.
			//
			ConstructorBuilder defCtorBuilder = ctx.TypeBuilder.DefineConstructor(
				MethodAttributes.Public,
				CallingConventions.Standard,
				Type.EmptyTypes);

			// Create object factory constructor.
			//
			ConstructorBuilder ofCtorBuilder = ctx.TypeBuilder.DefineConstructor(
				MethodAttributes.Public, 
				CallingConventions.Standard,
				_factoryParams);

			_defaultConstructorBuilder[type] = defCtorBuilder;
			_factoryConstructorBuilder[type] =  ofCtorBuilder;

			// Create method InitProperties
			//
			MethodBuilder initMethodBuilder = ctx.TypeBuilder.DefineMethod(
				"InitPropertiesOf" + type.Name,
				MethodAttributes.Private,
				typeof(void),
				Type.EmptyTypes);

			ctx.IniCtorGen = new MapGenerator(initCtorBuilder.   GetILGenerator());
			ctx.DefCtorGen = new MapGenerator(defCtorBuilder.    GetILGenerator());
			ctx.ObfCtorGen = new MapGenerator(ofCtorBuilder.     GetILGenerator());
			ctx.InitGen    = new MapGenerator(initMethodBuilder. GetILGenerator());

			// Scan properties.
			//
			PropertyInfo[] props = type.GetProperties(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

			foreach (PropertyInfo pi in props)
			{
				ctx.PropertyInfo  = pi;
				ctx.GetMethodInfo = pi.GetGetMethod(true);
				ctx.SetMethodInfo = pi.GetSetMethod(true);

				if (ctx.GetMethodInfo != null && ctx.GetMethodInfo.IsAbstract || 
					ctx.SetMethodInfo != null && ctx.SetMethodInfo.IsAbstract)
				{
					ctx.FieldType = pi.PropertyType;
					ctx.IsObject  = false;
					
					// Check for the MapTypeAttribute.
					//
					object[] attr = MapDescriptor.GetPropertyMapType(type, pi);

					if (attr != null)
					{
						foreach (MapTypeAttribute a in attr)
						{
							ctx.FieldType = a.MappedType;

							if (GetValueMember(ctx.FieldType, pi.PropertyType) != null)
							{
								ctx.IsObject = true;
								break;
							}
						}

						if (attr.Length > 0 && !ctx.IsObject)
						{
							throw new RsdnMapException(
								string.Format(
								"The '{0}' type does not have appropriate setter or getter member. " +
								"See '{1}' member of '{2}' type.",
								ctx.FieldType.Name,
								pi.Name,
								type.Name));
						}
					}

					// Create and initialize private field.
					//
					ctx.AttributeParams = MapDescriptor.GetPropertyParameters(pi);
					ctx.FieldBuilder    = ctx.TypeBuilder.DefineField(
						"_" + pi.Name, ctx.FieldType, FieldAttributes.Private);

					ctx.ParamBuilder = InitializeMemberParameters(ctx);

					InitializeAbstractClassField(ctx);
					CreateNonAbstractGettter    (ctx);
					CreateNonAbstractSetter     (ctx);
				}
			}

			ctx.DefCtorGen.ldarg_0.call(initMethodBuilder);
			ctx.ObfCtorGen.ldarg_0.call(initMethodBuilder);

			ConstructorInfo defParentCtor = GetDefaultConstructor(type);

			if (defParentCtor != null)
			{
				ctx.DefCtorGen.ldarg_0.call(defParentCtor);
			}

			// Find more appropriate object factory ctor.
			//
			ConstructorInfo parentCtor = type.GetConstructor(
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null, _factoryParams, null);

			if (parentCtor != null)
			{
				ctx.ObfCtorGen
					.ldarg_0
					.ldarg_s(1)
					.call(parentCtor)
					;
			}
			else
			{
				parentCtor = type.GetConstructor(
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
					null, Type.EmptyTypes, null);

				if (parentCtor != null)
				{
					ctx.ObfCtorGen
						.ldarg_0
						.call(parentCtor)
						;
				}
			}

			ctx.IniCtorGen.ret();
			ctx.DefCtorGen.ret();
			ctx.ObfCtorGen.ret();
			ctx.InitGen.   ret();

			// Create IMapEnumerable.GetCreatedObjects
			//
			MethodBuilder enumMethod = ctx.TypeBuilder.DefineMethod(
				"GetCreatedObjects",
				MethodAttributes.Virtual,
				typeof(object[]),
				Type.EmptyTypes);

			MapGenerator gen = new MapGenerator();

			gen.Gen = enumMethod.GetILGenerator();

			LocalBuilder arr = gen.DeclareLocal(typeof(object[]));

			gen
				.ldc_i4(ctx.Objects.Count)
				.newarr(typeof(object))
				.stloc_0
				.EndGen();

			for (int i = 0; i < ctx.Objects.Count; i++)
			{
				gen
					.ldloc_0
					.ldc_i4(i)
					.ldarg_0
					.ldfld((FieldBuilder)ctx.Objects[i])
					.stelem_ref
					.EndGen();
			}

			gen.ldloc_0.ret();

			ctx.TypeBuilder.DefineMethodOverride(
				enumMethod,
				typeof(IMapGenerated).GetMethod("GetCreatedMembers"));

			_defaultConstructorBuilder.Remove(type);
			_factoryConstructorBuilder.Remove(type);

			return ctx.TypeBuilder.CreateType();
		}

		static private void SetNonSerializedAttribute(FieldBuilder fieldBuilder)
		{
			ConstructorInfo        ci        = typeof(NonSerializedAttribute).GetConstructor(Type.EmptyTypes);
			CustomAttributeBuilder caBuilder = new CustomAttributeBuilder(ci, new object[0]);

			fieldBuilder.SetCustomAttribute(caBuilder);
		}

		static private FieldBuilder InitializeMemberParameters(GenContext ctx)
		{
			FieldBuilder paramBuilder = null;

			if (ctx.AttributeParams != null)
			{
				paramBuilder = ctx.TypeBuilder.DefineField(
					string.Format("_{0}_parameters", ctx.PropertyInfo.Name),
					typeof(object[]),
					FieldAttributes.Private | FieldAttributes.Static);

				SetNonSerializedAttribute(paramBuilder);

				ctx.IniCtorGen
					.ldtoken(ctx.TypeBuilder.BaseType)
					.call(typeof(Type),          "GetTypeFromHandle", typeof(RuntimeTypeHandle))
					.call(typeof(MapDescriptor), "GetDescriptor",     typeof(Type))
					.ldstr(ctx.PropertyInfo.Name)
					.callvirt(typeof(MapDescriptor), "GetPropertyParameters", typeof(string))
					.stsfld(paramBuilder)
					;
			}

			return paramBuilder;
		}

		class ConstructType 
		{
			public ConstructType(Type fieldType)
			{
				_fieldType = fieldType;

				if (MapEmit._defaultConstructorBuilder[fieldType] == null)
				{
					_constructType = fieldType.IsAbstract?
						MapDescriptor.GetDescriptor(fieldType).MappedType:
						fieldType;
				}
			}

			Type _fieldType;
			Type _constructType;

			public ConstructorInfo GetConstructor(Type[] types)
			{
				return
					_constructType == null?
					(ConstructorInfo)MapEmit._factoryConstructorBuilder[_fieldType]:
					_constructType.GetConstructor(
						BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
						null, types, null);
			}

			public ConstructorInfo GetDefaultConstructor()
			{
				return 
					_constructType == null?
					(ConstructorInfo)MapEmit._defaultConstructorBuilder[_fieldType]:
					MapEmit.GetDefaultConstructor(_constructType);
			}
		}

		static private void InitializeAbstractClassField(GenContext ctx)
		{
			if (ctx.FieldType.IsClass && ctx.FieldType != typeof(string))
			{
				if (MapDescriptor.GetDescriptor(ctx.FieldType) != null)
				{
					ConstructType constructType = new ConstructType(ctx.FieldType);

					bool callDefaultCtor = true;

					// Call MapDescriptor to construct the object.
					//
					if (ctx.FieldType.IsAbstract)
					{
						// Object factory construction.
						//
						FieldBuilder fieldBuilder = ctx.TypeBuilder.DefineField(
							string.Format("_{0}_MapDescriptor", ctx.PropertyInfo.Name),
							typeof(MapDescriptor),
							FieldAttributes.Private | FieldAttributes.Static);

						Label l1 = ctx.ObfCtorGen.DefineLabel();

						ctx.ObfCtorGen
							.ldsfld(fieldBuilder)
							.brtrue_s(l1)
							.ldtoken(ctx.FieldType)
							.call(typeof(Type),          "GetTypeFromHandle", typeof(RuntimeTypeHandle))
							.call(typeof(MapDescriptor), "GetDescriptor",     typeof(Type))
							.stsfld(fieldBuilder)
							.MarkLabel(l1)
							.ldarg_0
							.ldsfld(fieldBuilder)
							.ldarg_1
							.EndGen();

						MethodInfo memberParams = 
							typeof(MapInitializingData).GetProperty("MemberParameters").GetSetMethod();

						if (ctx.ParamBuilder != null)
						{
							ctx.ObfCtorGen
								.ldarg_1
								.ldsfld(ctx.ParamBuilder)
								.callvirt(memberParams);
						}

						ctx.ObfCtorGen
							.callvirt(typeof(MapDescriptor), "CreateInstanceEx", _factoryParams)
							.isinst(ctx.FieldType)
							.stfld(ctx.FieldBuilder);

						if (ctx.ParamBuilder != null)
						{
							ctx.ObfCtorGen
								.ldarg_1
								.ldnull
								.callvirt(memberParams);
						}

						if (ctx.ParamBuilder != null)
						{
							// Default constructor.
							//
							if (ctx.InitDataField == null)
							{
								ctx.InitDataField = ctx.DefCtorGen.DeclareLocal(typeof(MapInitializingData));

								ctx.DefCtorGen
									.newobj(GetDefaultConstructor(typeof(MapInitializingData)))
									.stloc_0
									.EndGen();
							}

							Label l2 = ctx.DefCtorGen.DefineLabel();

							ctx.DefCtorGen
								.ldsfld(fieldBuilder)
								.brtrue_s(l1)
								.ldtoken(ctx.FieldType)
								.call(typeof(Type),          "GetTypeFromHandle", typeof(RuntimeTypeHandle))
								.call(typeof(MapDescriptor), "GetDescriptor",     typeof(Type))
								.stsfld(fieldBuilder)
								.MarkLabel(l1)

								.ldloc_0
								.ldsfld(ctx.ParamBuilder)
								.callvirt(memberParams)
								
								.ldarg_0
								.ldsfld(fieldBuilder)
								.ldloc_0
								.callvirt(typeof(MapDescriptor), "CreateInstanceEx", _factoryParams)
								.isinst(ctx.FieldType)
								.stfld(ctx.FieldBuilder)

								.ldloc_0
								.ldnull
								.callvirt(memberParams);

							callDefaultCtor = false;
						}
					} 
					else
					{
						ConstructorInfo ofci = null;

						if (ctx.AttributeParams != null)
						{
							Type[] types = new Type[ctx.AttributeParams.Length];

							for (int i = 0; i < ctx.AttributeParams.Length; i++)
								types[i] = ctx.AttributeParams[i] != null?
									ctx.AttributeParams[i].GetType(): typeof(object);

							ofci = constructType.GetConstructor(types);

							if (ofci != null)
							{
								callDefaultCtor = false;

								ctx.DefCtorGen.ldarg_0.EndGen();
								ctx.ObfCtorGen.ldarg_0.EndGen();

								for (int i = 0; i < ctx.AttributeParams.Length; i++)
								{
									object o = ctx.AttributeParams[i];

									if (ctx.DefCtorGen.LoadObject(o) == false)
									{
										ctx.DefCtorGen
											.ldsfld(ctx.ParamBuilder)
											.ldc_i4(i)
											.ldelem_ref
											.CastTo(types[i]);
									}

									if (ctx.ObfCtorGen.LoadObject(o) == false)
									{
										ctx.ObfCtorGen
											.ldsfld(ctx.ParamBuilder)
											.ldc_i4(i)
											.ldelem_ref
											.CastTo(types[i]);
									}
								}

								ctx.DefCtorGen
									.newobj(ofci)
									.stfld(ctx.FieldBuilder);

								ctx.ObfCtorGen
									.newobj(ofci)
									.stfld(ctx.FieldBuilder);
							}
						}

						if (ofci == null)
						{
							ofci = constructType.GetConstructor(_factoryParams);

							if (ofci != null)
							{
								MethodInfo memberParams = 
									typeof(MapInitializingData).GetProperty("MemberParameters").GetSetMethod();

								// Object factory constructor.
								//
								if (ctx.ParamBuilder != null)
								{
									ctx.ObfCtorGen
										.ldarg_1
										.ldsfld(ctx.ParamBuilder)
										.callvirt(memberParams);
								}

								ctx.ObfCtorGen
									.ldarg_0
									.ldarg_1
									.newobj(ofci)
									.stfld(ctx.FieldBuilder);

								if (ctx.ParamBuilder != null)
								{
									ctx.ObfCtorGen
										.ldarg_1
										.ldnull
										.callvirt(memberParams);
								}

								if (ctx.ParamBuilder != null)
								{
									// Default constructor.
									//
									if (ctx.InitDataField == null)
									{
										ctx.InitDataField = ctx.DefCtorGen.DeclareLocal(typeof(MapInitializingData));

										ctx.DefCtorGen
											.newobj(GetDefaultConstructor(typeof(MapInitializingData)))
											.stloc_0
											.EndGen();
									}

									ctx.DefCtorGen
										.ldloc_0
										.ldsfld(ctx.ParamBuilder)
										.callvirt(memberParams)
										.ldarg_0
										.ldloc_0
										.newobj(ofci)
										.stfld(ctx.FieldBuilder)
										.ldloc_0
										.ldnull
										.callvirt(memberParams);
								}
							}
							else
							{
								ofci = constructType.GetConstructor(Type.EmptyTypes);

								if (ofci != null)
								{
									ctx.ObfCtorGen
										.ldarg_0
										.newobj(ofci)
										.stfld(ctx.FieldBuilder);
								}
							}
						}
					}

					if (callDefaultCtor)
					{
						// Default construction.
						//
						ConstructorInfo ci = constructType.GetDefaultConstructor();

						if (ci == null)
						{
							throw new RsdnMapException(
								string.Format("The '{0}' type has to have public default constructor.",
								ctx.FieldType.Name));
						}

						// Call default member constructor inside default constractor of the type.
						//
						ctx.DefCtorGen
							.ldarg_0
							.newobj(ci)
							.stfld(ctx.FieldBuilder);
					}

					if (ctx.FieldType.GetInterface(typeof(IMapSetPropertyInfo).Name) != null)
					{
						FieldBuilder initFieldBuilder = ctx.TypeBuilder.DefineField(
							string.Format("_{0}_SetPropertyInfo", ctx.PropertyInfo.Name),
							typeof(IMapSetPropertyInfo),
							FieldAttributes.Private | FieldAttributes.Static);

						SetNonSerializedAttribute(initFieldBuilder);

						ConstructorInfo ci = typeof(MapPropertyInfo).GetConstructor(new Type[] { typeof(Type), typeof(string) });

						ctx.IniCtorGen
							.ldtoken(ctx.PropertyInfo.PropertyType)
							.call(typeof(Type), "GetTypeFromHandle", typeof(RuntimeTypeHandle))
							.ldstr(ctx.PropertyInfo.Name)
							.newobj(ci)
							.stsfld(initFieldBuilder);

						ctx.InitGen
							.ldarg_0
							.ldfld(ctx.FieldBuilder)
							.castclass(typeof(IMapSetPropertyInfo))
							.ldsfld(initFieldBuilder)
							.ldarg_0
							.callvirt(typeof(IMapSetPropertyInfo), "SetInfo", typeof(MapPropertyInfo), typeof(object));
					}
				}

				ctx.Objects.Add(ctx.FieldBuilder);
			}
		}

		static private void CreateNonAbstractSetter(GenContext ctx)
		{
			MemberInfo member = 
				ctx.IsObject? GetValueMember(ctx.FieldType, ctx.PropertyInfo.PropertyType): null;

			if (ctx.SetMethodInfo == null && ctx.IsObject && ((PropertyInfo)member).GetSetMethod() == null)
				return;

			MethodBuilder setMethod = ctx.TypeBuilder.DefineMethod(
				ctx.SetMethodInfo != null? ctx.SetMethodInfo.Name: "set_" + ctx.PropertyInfo.Name,
				MethodAttributes.Public |
				MethodAttributes.Virtual |
				MethodAttributes.HideBySig |
				MethodAttributes.SpecialName,
				null,
				new Type[] { ctx.PropertyInfo.PropertyType});

			MapGenerator gen = new MapGenerator(setMethod.GetILGenerator());

			gen
				.ldarg_0
				.EndGen();

			if (ctx.IsObject)
			{
				gen
					.ldfld(ctx.FieldBuilder)
					.ldarg_1
					.EndGen();

				if (member is PropertyInfo)
				{
					gen.callvirt(((PropertyInfo)member).GetSetMethod());
				}
				else
				{
					gen.stfld((FieldInfo)member);
				}
			}
			else
			{
				gen
					.ldarg_1
					.stfld(ctx.FieldBuilder);
			}

			gen.ret();
		}

		static private void CreateNonAbstractGettter(GenContext ctx)
		{
			MethodBuilder getMethod = ctx.TypeBuilder.DefineMethod(
				ctx.GetMethodInfo != null? ctx.GetMethodInfo.Name: "get_" + ctx.PropertyInfo.Name,
				MethodAttributes.Public | 
				MethodAttributes.Virtual |
				MethodAttributes.HideBySig | 
				MethodAttributes.SpecialName,
				ctx.PropertyInfo.PropertyType,
				Type.EmptyTypes);

			MapGenerator gen = new MapGenerator(getMethod.GetILGenerator());

			gen
				.ldarg_0
				.ldfld(ctx.FieldBuilder);

			if (ctx.IsObject)
			{
				MemberInfo member = GetValueMember(ctx.FieldType, ctx.PropertyInfo.PropertyType);

				if (member is PropertyInfo)
				{
					PropertyInfo mpi = ((PropertyInfo)member);

					gen.callvirt(mpi.GetGetMethod());

					if (mpi.PropertyType != ctx.PropertyInfo.PropertyType && 
						!ctx.PropertyInfo.PropertyType.IsEnum)
					{
						gen.castclass(ctx.PropertyInfo.PropertyType);
					}
				}
				else
				{
					FieldInfo mfi = (FieldInfo)member;

					gen.ldfld((FieldInfo)member);

					if (mfi.FieldType != ctx.PropertyInfo.PropertyType && 
						!ctx.PropertyInfo.PropertyType.IsEnum)
					{
						gen.castclass(mfi.FieldType);
					}
				}
			}

			gen.ret();
		}

		internal static MapDescriptor CreateDescriptor(Type type)
		{
			Type         originalType = type;
			AssemblyName assemblyName = new AssemblyName();
			string       assemblyDir  = Path.GetDirectoryName(type.Module.FullyQualifiedName);
			string       fullName     = type.FullName.Replace('+', '.');

			assemblyName.Name = fullName + ".MapDescriptor.dll";

			AssemblyBuilder assemblyBuilder = Thread.GetDomain().DefineDynamicAssembly(
				assemblyName,
				AssemblyBuilderAccess.RunAndSave,
				assemblyDir);

			ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name);

			// Create non abstract class, if the current one is abstract.
			//
			if (type.IsAbstract)
				type = CreateNonAbstractType(type, moduleBuilder);

			// Create mapping extension MapDescriptor.
			//
			TypeBuilder typeBuilder = moduleBuilder.DefineType(
				fullName + ".Descriptor", TypeAttributes.Public, typeof(MapDescriptor));

			CreateCreateInstanceMethodOfDescriptor(type, typeBuilder);
			CreateCreateInstance4MethodOfDescriptor(type, typeBuilder);

			ConstructorBuilder ctorBuilder =
				typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);

			// Create the type descriptor.
			//
			Type descriptorType = typeBuilder.CreateType();

			MapDescriptor desc = (MapDescriptor)Activator.CreateInstance(descriptorType);
			desc.InitMembers(originalType, type, moduleBuilder);

#if DEBUG
			try
			{
				assemblyBuilder.Save(assemblyName.Name);

				System.Diagnostics.Debug.WriteLine(
					string.Format("The '{0}' type saved in '{1}\\{2}'.",
					fullName,
					assemblyDir,
					assemblyName.Name));
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine(
					string.Format("Can't save the '{0}' assembly for the '{1}' type in '{2}': {3}.", 
					assemblyName.Name,
					fullName,
					assemblyDir,
					ex.Message));
			}
#endif

			return desc;
		}

		static private void CreateCreateInstanceMethodOfDescriptor(Type type, TypeBuilder typeBuilder)
		{
			// Create CreateInstance Method
			//
			ConstructorInfo ci = GetDefaultConstructor(type);

			if (ci == null || !ci.IsPublic)
			{
				System.Diagnostics.Debug.WriteLine(
					string.Format("The '{0}' type must have the public default constructor.", type.Name));
			}
			else
			{
				MethodBuilder methodBuilder = typeBuilder.DefineMethod(
					"CreateInstance",
					MethodAttributes.Public | 
					MethodAttributes.Virtual |
					MethodAttributes.HideBySig,
					typeof(object),
					Type.EmptyTypes);
			
				MapGenerator gen = new MapGenerator(methodBuilder.GetILGenerator());

				LocalBuilder l0 = gen.DeclareLocal(typeof(Exception));
				LocalBuilder l1 = gen.DeclareLocal(typeof(object));

				gen
					// try
					// {
					.BeginExceptionBlock

					// return new BizObject();
					//
					.newobj(ci)
					.stloc_1

					// catch (Exception ex)
					// {
					.BeginCatchBlock_Exception

					.stloc_0
					.ldloc_0
					.call(typeof(MapDescriptor), "HandleException", BindingFlags.Static | BindingFlags.NonPublic)
					.ldnull
					.stloc_1

					.EndExceptionBlock

					.ldloc_1
					.ret();
			}
		}

		static private void CreateCreateInstance4MethodOfDescriptor(Type type, TypeBuilder typeBuilder)
		{
			ConstructorInfo ci = type.GetConstructor(
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, 
				null, _factoryParams, null);

			bool addParameter = false;

			if (ci != null && ci.IsPublic)
			{
				addParameter = true;
			}
			else
			{
				ci = type.GetConstructor(
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, 
					null, Type.EmptyTypes, null);

				if (ci == null || !ci.IsPublic)
				{
					System.Diagnostics.Debug.WriteLine(
						string.Format("The '{0}' type must have the public default constructor.", type.Name));

					return;
				}
			}

			MethodBuilder methodBuilder = typeBuilder.DefineMethod(
				"CreateInstance",
				MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
				typeof(object),
				_factoryParams);

			MapGenerator gen = new MapGenerator(methodBuilder.GetILGenerator());

			LocalBuilder l0 = gen.DeclareLocal(typeof(Exception));
			LocalBuilder l1 = gen.DeclareLocal(typeof(object));

			gen
				// try
				// {
				.BeginExceptionBlock.EndGen();

			// return new BizObject();
			//
			if (addParameter)
				gen.ldarg_s(1);

			gen
				.newobj(ci)
				.stloc_1

				// catch (Exception ex)
				// {
				.BeginCatchBlock_Exception

				.stloc_0
				.ldloc_0
				.call(typeof(MapDescriptor), "HandleException", BindingFlags.Static | BindingFlags.NonPublic)
				.ldnull
				.stloc_1

				.EndExceptionBlock

				.ldloc_1
				.ret();
		}

		internal static object CreateMemberMapper(
			Type          objectType,
			Type          memberType,
			string        memberName,
			bool          isProperty,
			bool          createGet,
			bool          createSet,
			ModuleBuilder moduleBuilder)
		{
			string fullName = objectType.FullName.Replace('+', '.');

			TypeBuilder typeBuilder = moduleBuilder.DefineType(
				fullName + ".MemberDescriptor." + memberName,
				TypeAttributes.Public,
				isProperty? typeof(PropertyMapper): typeof(FieldMapper));

			// Create default constructor.
			//
			ConstructorBuilder ctorBuilder =
				typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);

			MethodInfo getMethod    = null;
			Type       definingType = objectType;

			if (isProperty)
			{
				// Check if mapping extension property was created.
				//
				if (!createGet)
				{
					getMethod = objectType.GetMethod("get_" + memberName);
					createSet = getMethod != null;
				}

				if (createGet && getMethod == null)
				{
					getMethod = objectType.GetProperty(memberName).GetGetMethod();

					if (getMethod == null)
					{
						createGet = false;
					}
					else
					{
						definingType = getMethod.DeclaringType;

						if (definingType == objectType && objectType.BaseType.IsAbstract)
						{
							PropertyInfo pi = objectType.BaseType.GetProperty(memberName);

							if (pi != null)
							{
								MethodInfo mi = pi.GetGetMethod();

								if (mi != null)
									getMethod = mi;
							}
						}
					}
				}
			}

			// Create GetValue method.
			//
			if (createGet)
			{
				CreateGetter(memberType, typeBuilder, objectType, isProperty, getMethod, memberName);
			}

			MethodInfo setMethod = null;

			if (isProperty)
			{
				// Check if mapping extension property was created.
				//
				if (!createSet)
				{
					setMethod = objectType.GetMethod("set_" + memberName);
					createSet = setMethod != null;
				}

				if (createSet && setMethod == null)
				{
					setMethod = objectType.GetProperty(memberName).GetSetMethod();
				}
			}

			// Create SetValue method.
			//
			if (createSet)
			{
				CreateSetter(memberType, typeBuilder, objectType, isProperty, setMethod, memberName);
			}

			Type descriptorType = typeBuilder.CreateType();

			return Activator.CreateInstance(descriptorType);
		}

		static private void CreateSetter(
			Type        memberType,
			TypeBuilder typeBuilder,
			Type        objectType,
			bool        isProperty,
			MethodInfo  setMethod,
			string      memberName)
		{
			if (memberType == typeof(string)    || memberType == typeof(bool)    ||
				memberType == typeof(byte)      || memberType == typeof(char)    ||
				memberType == typeof(DateTime)  || memberType == typeof(decimal) ||
				memberType == typeof(double)    || memberType == typeof(Int16)   ||
				memberType == typeof(Int32)     || memberType == typeof(Int64)   ||
				memberType == typeof(sbyte)     || memberType == typeof(float)   ||
				memberType == typeof(UInt16)    || memberType == typeof(UInt32)  ||
				memberType == typeof(UInt64)    || memberType == typeof(Guid)    ||
				memberType.IsEnum               || IsNullableType(memberType))
			{
				MethodBuilder methodBuilder = typeBuilder.DefineMethod(
					"SetValue",
					MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
					null,
					new Type[] { typeof(object), typeof(object) } );
			
				MapGenerator gen = new MapGenerator(methodBuilder.GetILGenerator());

				// code
				//
				gen
					.ldarg_1
					.castclass(objectType);

				if (IsNullableType(memberType))
				{
					PropertyInfo    pi = memberType.GetProperty("Value");
					FieldInfo       fi = memberType.GetField   ("Null");
					ConstructorInfo ci = memberType.GetConstructor(new Type[] { pi.PropertyType } );

					// IL_0000:  ldarg.1    
					// IL_0001:  castclass  class AllTest.Dest

					Label l1 = gen.DefineLabel();
					Label l2 = gen.DefineLabel();
					Label l3 = gen.DefineLabel();

					gen
						.ldarg_2                    // IL_0006:  ldarg.2    
						.brfalse_s(l1)              // IL_0007:  brfalse.s  IL_002b
						.ldarg_2                    // IL_0009:  ldarg.2    
						.IsDBNull                   // IL_000a:  isinst     class [mscorlib]System.DBNull
						.brtrue_s(l1)               // IL_000f:  brtrue.s   IL_004d
						.ldarg_2                    // IL_0011:  ldarg.2    

						.isinst(memberType)         // IL_000a:  isinst     valuetype SqlInt32
						.brtrue_s(l2)               // IL_000f:  brtrue.s   IL_001e

						.ldarg_2                    // IL_0011:  ldarg.2
						.ConvertTo(pi.PropertyType) // IL_0012:  call       int32 [mscorlib]System.Convert::ToInt32(object)
						.newobj(ci)                 // IL_0017:  newobj     instance void SqlInt32::.ctor(int32)
						.br_s(l3)                   // IL_001c:  br.s       IL_0029

						.MarkLabel(l2)
					
						.ldarg_2                    // IL_001e:  ldarg.2    
						.unbox(memberType)          // IL_001f:  unbox      [System.Data]System.Data.SqlTypes.SqlInt32
						.ldobj(memberType)          // IL_0024:  ldobj      [System.Data]System.Data.SqlTypes.SqlInt32
						.br_s(l3)                   // IL_0029:  br.s       IL_0030

						.MarkLabel(l1)

						.ldsfld(fi)                 // IL_002b:  ldsfld     valuetype SqlInt32 SqlInt32::Null
					
						.MarkLabel(l3);

					// IL_0030:  stfld      valuetype [System.Data]System.Data.SqlTypes.SqlInt32 AllTest.Dest::m_int
					// IL_0035:  ret        
				}
				else
				{
					gen
						.ldarg_0
						.ldarg_2
						.call(typeof(BaseMemberMapper), "MapFrom", typeof(object));

					if (memberType.IsEnum)
						gen.UnboxIfValueType(memberType).ldind_i4.EndGen();
					else
						gen.ConvertTo(memberType);
				}

				if (isProperty)
				{
					gen.callvirt(setMethod);
				}
				else
				{
					gen.stfld(objectType, memberName);
				}

				gen.ret();
			}
		}

		static private void CreateGetter(
			Type        memberType,
			TypeBuilder typeBuilder,
			Type        objectType,
			bool        isProperty,
			MethodInfo  getMethod,
			string      memberName)
		{
			PropertyInfo piIsNull = memberType.GetProperty("IsNull");
			PropertyInfo piValue  = memberType.GetProperty("Value");

			MethodBuilder methodBuilder = typeBuilder.DefineMethod(
				"GetValue",
				MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
				typeof(object),
				new Type[] { typeof(object) } );
			
			MapGenerator gen = new MapGenerator(methodBuilder.GetILGenerator());

			gen
				.ldarg_0
				.ldarg_1
				.castclass(getMethod != null? getMethod.DeclaringType: objectType)
				.EndGen();

			if (isProperty)
			{
				gen.callvirt(getMethod);
			}
			else
			{
				gen.ldfld(objectType, memberName);
			}

			Label end = gen.DefineLabel();

			if (piIsNull != null)
			{
				LocalBuilder local = gen.DeclareLocal(memberType);
				Label        l1    = gen.DefineLabel();

				gen
					.stloc_0
					.ldloca_s(0)

					.call(piIsNull.GetGetMethod())
					.brfalse_s(l1)
					.ldnull
					.br_s(end)

					.MarkLabel(l1)
					.ldloca_s(0);
			}

			Type retType = memberType;

			if (piValue != null)
			{
				retType = piValue.PropertyType;

				gen.call(piValue.GetGetMethod());
			}

			gen
				.BoxIfValueType(retType)

				.MarkLabel(end)

				.call(typeof(BaseMemberMapper), "MapTo", typeof(object))
				.ret();
		}

		private static bool IsNullableType(Type type)
		{
			return 
				type.GetInterface("INullable") != null &&
				type.GetProperty ("Value")     != null &&
				type.GetField    ("Null")      != null;
		}
	}
}