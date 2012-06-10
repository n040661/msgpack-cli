#region -- License Terms --
//
// MessagePack for CLI
//
// Copyright (C) 2010 FUJIWARA, Yusuke
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//        http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
//
#endregion -- License Terms --

using System;
using System.Collections;
using System.Diagnostics.Contracts;
using System.Linq.Expressions;
using System.Text;
using System.IO;

namespace MsgPack.Serialization.ExpressionSerializers
{
	/// <summary>
	///		<see cref="MessagePackSerializer{T}"/> for a sequential collection using expression tree.
	/// </summary>
	/// <typeparam name="T">The type of element.</typeparam>
	internal abstract class SequenceExpressionMessagePackSerializer<T> : MessagePackSerializer<T>, IExpressionMessagePackSerializer
	{
		private readonly Func<T, int> _getCount;
		private readonly CollectionTraits _traits;

		protected CollectionTraits Traits
		{
			get { return this._traits; }
		}

		private readonly IMessagePackSerializer _elementSerializer;

		private readonly Action<Packer, T, IMessagePackSerializer> _packToCore;
		private readonly Action<Unpacker, T, IMessagePackSerializer, int> _unpackToCore;
		private readonly Expression _packToCoreExpression;
		private readonly Expression _unpackToCoreExpression;

		protected SequenceExpressionMessagePackSerializer( SerializationContext context, CollectionTraits traits )
		{
			Contract.Assert( typeof( T ).IsArray || typeof( T ) is IEnumerable, typeof( T ) + " is not array nor IEnumerable" );
			this._traits = traits;
			this._elementSerializer = context.GetSerializer( traits.ElementType );
			this._getCount = ExpressionSerializerLogics.CreateGetCount<T>( traits );

			var packerParameter = Expression.Parameter( typeof( Packer ), "packer" );
			var objectTreeParameter = Expression.Parameter( typeof( T ), "objectTree" );
			var elementSerializerParameter = Expression.Parameter( typeof( IMessagePackSerializer ), "elementSerializer" );
			var elementSerializerType = typeof( MessagePackSerializer<> ).MakeGenericType( traits.ElementType );

			/*
			 *	packer.PackArrayHeader( objectTree.Count() );
			 *	foreach( var item in objectTree )
			 *	{
			 *		elementSerializer.PackTo( packer, item );
			 *	}
			 */
			var packToCore =
				Expression.Lambda<Action<Packer, T, IMessagePackSerializer>>(
					Expression.Block(
						Expression.Call(
							packerParameter,
							Metadata._Packer.PackArrayHeader,
							ExpressionSerializerLogics.CreateGetCountExpression<T>( traits, objectTreeParameter )
						),
						traits.AddMethod == null
						? ExpressionSerializerLogics.For(
							Expression.ArrayLength( objectTreeParameter ),
							indexVariable =>
								Expression.Call(
									Expression.TypeAs( elementSerializerParameter, elementSerializerType ),
									typeof( MessagePackSerializer<> ).MakeGenericType( traits.ElementType ).GetMethod( "PackTo" ),
									packerParameter,
									Expression.ArrayIndex( objectTreeParameter, indexVariable )
								)
						)
						: ExpressionSerializerLogics.ForEach(
							objectTreeParameter,
							traits,
							elementVariable =>
								Expression.Call(
									Expression.TypeAs( elementSerializerParameter, elementSerializerType ),
									typeof( MessagePackSerializer<> ).MakeGenericType( traits.ElementType ).GetMethod( "PackTo" ),
									packerParameter,
									elementVariable
								)
						)
					),
					packerParameter, objectTreeParameter, elementSerializerParameter
				);

			if ( context.GeneratorOption == SerializationMethodGeneratorOption.CanDump )
			{
				this._packToCoreExpression = packToCore;
			}

			this._packToCore = packToCore.Compile();

			var unpackerParameter = Expression.Parameter( typeof( Unpacker ), "unpacker" );
			var instanceParameter = Expression.Parameter( typeof( T ), "instance" );
			var countParamter = Expression.Parameter( typeof( int ), "count" );

			/*
			 *	for ( int i = 0; i < count; i++ )
			 *	{
			 *		if ( !unpacker.Read() )
			 *		{
			 *			throw SerializationExceptions.NewMissingItem( i );
			 *		}
			 *	
			 *		T item;
			 *		if ( !unpacker.IsArrayHeader && !unpacker.IsMapHeader )
			 *		{
			 *			item = this.ElementSerializer.UnpackFrom( unpacker );
			 *		}
			 *		else
			 *		{
			 *			using ( Unpacker subtreeUnpacker = unpacker.ReadSubtree() )
			 *			{
			 *				item = this.ElementSerializer.UnpackFrom( subtreeUnpacker );
			 *			}
			 *		}
			 *
			 *		instance[ i ] = item; -- OR -- instance.Add( item );
			 *	}
			 */

			var itemVariable = Expression.Variable( traits.ElementType, "item" );
			var unpackFrom = typeof( MessagePackSerializer<> ).MakeGenericType( traits.ElementType ).GetMethod( "UnpackFrom" );
			var unpackToCore =
				Expression.Lambda<Action<Unpacker, T, IMessagePackSerializer, int>>(
					ExpressionSerializerLogics.For(
						countParamter,
						indexVariable =>
							Expression.Block(
								new[] { itemVariable },
								Expression.IfThen(
									Expression.IsFalse(
										Expression.Call( unpackerParameter, Metadata._Unpacker.Read )
									),
									Expression.Throw(
										Expression.Call( SerializationExceptions.NewMissingItemMethod, indexVariable )
									)
								),
								Expression.Assign(
									itemVariable,
									ExpressionSerializerLogics.CreateUnpackItem( unpackerParameter, unpackFrom, elementSerializerParameter, elementSerializerType )
								),
								traits.AddMethod == null
								? Expression.Assign( // Array
									Expression.ArrayAccess( instanceParameter, indexVariable ),
									itemVariable
								) : Expression.Call( // List
									instanceParameter,
									traits.AddMethod,
									itemVariable
								) as Expression
							)
					),
					unpackerParameter, instanceParameter, elementSerializerParameter, countParamter
				);

			if ( context.GeneratorOption == SerializationMethodGeneratorOption.CanDump )
			{
				this._unpackToCoreExpression = unpackToCore;
			}

			this._unpackToCore = unpackToCore.Compile();
		}

		protected internal override void PackToCore( Packer packer, T objectTree )
		{
			this._packToCore( packer, objectTree, this._elementSerializer );
		}

		protected internal override void UnpackToCore( Unpacker unpacker, T collection )
		{
			this._unpackToCore( unpacker, collection, this._elementSerializer, UnpackHelpers.GetItemsCount( unpacker ) );
		}

		public override string ToString()
		{
			if ( this._packToCoreExpression == null || this._unpackToCoreExpression == null )
			{
				return base.ToString();
			}
			else
			{
				var buffer = new StringBuilder( Int16.MaxValue );
				using ( var writer = new StringWriter( buffer ) )
				{
					this.ToStringCore( writer, 0 );
				}

				return buffer.ToString();
			}
		}

		void IExpressionMessagePackSerializer.ToString( TextWriter writer, int depth )
		{
			this.ToStringCore( writer ?? TextWriter.Null, depth < 0 ? 0 : depth );
		}

		private void ToStringCore( TextWriter writer, int depth )
		{
			var name = this.GetType().Name;
			int indexOfAgusam = name.IndexOf( '`' );
			int nameLength = indexOfAgusam < 0 ? name.Length : indexOfAgusam;
			for ( int i = 0; i < nameLength; i++ )
			{
				writer.Write( name[ i ] );
			}

			writer.Write( "For" );
			writer.WriteLine( typeof( T ) );

			ExpressionDumper.WriteIndent( writer, depth + 1 );
			writer.Write( "PackToCore : " );
			new ExpressionDumper( writer, depth + 2 ).Visit( this._packToCoreExpression );
			writer.WriteLine();

			ExpressionDumper.WriteIndent( writer, depth + 1 );
			writer.Write( "UnpackToCore : " );
			new ExpressionDumper( writer, depth + 2 ).Visit( this._unpackToCoreExpression );
		}
	}
}