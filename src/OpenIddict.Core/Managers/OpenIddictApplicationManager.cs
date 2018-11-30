﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/openiddict-core for more information concerning
 * the license and the contributors participating to this project.
 */

using System;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CryptoHelper;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace OpenIddict.Core
{
    /// <summary>
    /// Provides methods allowing to manage the applications stored in the store.
    /// </summary>
    /// <typeparam name="TApplication">The type of the Application entity.</typeparam>
    public class OpenIddictApplicationManager<TApplication> : IOpenIddictApplicationManager where TApplication : class
    {
        public OpenIddictApplicationManager(
            [NotNull] IOpenIddictApplicationCache<TApplication> cache,
            [NotNull] IOpenIddictApplicationStoreResolver resolver,
            [NotNull] ILogger<OpenIddictApplicationManager<TApplication>> logger,
            [NotNull] IOptionsMonitor<OpenIddictCoreOptions> options)
        {
            Cache = cache;
            Store = resolver.Get<TApplication>();
            Logger = logger;
            Options = options;
        }

        /// <summary>
        /// Gets the cache associated with the current manager.
        /// </summary>
        protected IOpenIddictApplicationCache<TApplication> Cache { get; }

        /// <summary>
        /// Gets the logger associated with the current manager.
        /// </summary>
        protected ILogger Logger { get; }

        /// <summary>
        /// Gets the options associated with the current manager.
        /// </summary>
        protected IOptionsMonitor<OpenIddictCoreOptions> Options { get; }

        /// <summary>
        /// Gets the store associated with the current manager.
        /// </summary>
        protected IOpenIddictApplicationStore<TApplication> Store { get; }

        /// <summary>
        /// Determines the number of applications that exist in the database.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the number of applications in the database.
        /// </returns>
        public virtual Task<long> CountAsync(CancellationToken cancellationToken = default)
            => Store.CountAsync(cancellationToken);

        /// <summary>
        /// Determines the number of applications that match the specified query.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="query">The query to execute.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the number of applications that match the specified query.
        /// </returns>
        public virtual Task<long> CountAsync<TResult>(
            [NotNull] Func<IQueryable<TApplication>, IQueryable<TResult>> query, CancellationToken cancellationToken = default)
        {
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            return Store.CountAsync(query, cancellationToken);
        }

        /// <summary>
        /// Creates a new application.
        /// </summary>
        /// <param name="application">The application to create.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual Task CreateAsync([NotNull] TApplication application, CancellationToken cancellationToken = default)
            => CreateAsync(application, secret: null, cancellationToken);

        /// <summary>
        /// Creates a new application.
        /// Note: the default implementation automatically hashes the client
        /// secret before storing it in the database, for security reasons.
        /// </summary>
        /// <param name="application">The application to create.</param>
        /// <param name="secret">The client secret associated with the application, if applicable.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual async Task CreateAsync(
            [NotNull] TApplication application,
            [CanBeNull] string secret, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            if (!string.IsNullOrEmpty(await Store.GetClientSecretAsync(application, cancellationToken)))
            {
                throw new ArgumentException("The client secret hash cannot be set on the application entity.", nameof(application));
            }

            // If no client type was specified, assume it's a public application if no secret was provided.
            var type = await Store.GetClientTypeAsync(application, cancellationToken);
            if (string.IsNullOrEmpty(type))
            {
                await Store.SetClientTypeAsync(application, string.IsNullOrEmpty(secret) ?
                    OpenIddictConstants.ClientTypes.Public :
                    OpenIddictConstants.ClientTypes.Confidential, cancellationToken);
            }

            // If a client secret was provided, obfuscate it.
            if (!string.IsNullOrEmpty(secret))
            {
                secret = await ObfuscateClientSecretAsync(secret, cancellationToken);
                await Store.SetClientSecretAsync(application, secret, cancellationToken);
            }

            var results = await ValidateAsync(application, cancellationToken);
            if (results.Any(result => result != ValidationResult.Success))
            {
                var builder = new StringBuilder();
                builder.AppendLine("One or more validation error(s) occurred while trying to create a new application:");
                builder.AppendLine();

                foreach (var result in results)
                {
                    builder.AppendLine(result.ErrorMessage);
                }

                throw new OpenIddictExceptions.ValidationException(builder.ToString(), results);
            }

            await Store.CreateAsync(application, cancellationToken);

            if (!Options.CurrentValue.DisableEntityCaching)
            {
                await Cache.AddAsync(application, cancellationToken);
            }
        }

        /// <summary>
        /// Creates a new application based on the specified descriptor.
        /// Note: the default implementation automatically hashes the client
        /// secret before storing it in the database, for security reasons.
        /// </summary>
        /// <param name="descriptor">The application descriptor.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the unique identifier associated with the application.
        /// </returns>
        public virtual async Task<TApplication> CreateAsync(
            [NotNull] OpenIddictApplicationDescriptor descriptor, CancellationToken cancellationToken = default)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            var application = await Store.InstantiateAsync(cancellationToken);
            if (application == null)
            {
                throw new InvalidOperationException("An error occurred while trying to create a new application.");
            }

            await PopulateAsync(application, descriptor, cancellationToken);

            var secret = await Store.GetClientSecretAsync(application, cancellationToken);
            if (!string.IsNullOrEmpty(secret))
            {
                await Store.SetClientSecretAsync(application, secret: null, cancellationToken);
                await CreateAsync(application, secret, cancellationToken);
            }
            else
            {
                await CreateAsync(application, cancellationToken);
            }

            return application;
        }

        /// <summary>
        /// Removes an existing application.
        /// </summary>
        /// <param name="application">The application to delete.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual async Task DeleteAsync([NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            if (!Options.CurrentValue.DisableEntityCaching)
            {
                await Cache.RemoveAsync(application, cancellationToken);
            }

            await Store.DeleteAsync(application, cancellationToken);
        }

        /// <summary>
        /// Retrieves an application using its client identifier.
        /// </summary>
        /// <param name="identifier">The client identifier associated with the application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the client application corresponding to the identifier.
        /// </returns>
        public virtual async Task<TApplication> FindByClientIdAsync(
            [NotNull] string identifier, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                throw new ArgumentException("The identifier cannot be null or empty.", nameof(identifier));
            }

            var application = Options.CurrentValue.DisableEntityCaching ?
                await Store.FindByClientIdAsync(identifier, cancellationToken) :
                await Cache.FindByClientIdAsync(identifier, cancellationToken);

            if (application == null)
            {
                return null;
            }

            // SQL engines like Microsoft SQL Server or MySQL are known to use case-insensitive lookups by default.
            // To ensure a case-sensitive comparison is enforced independently of the database/table/query collation
            // used by the store, a second pass using string.Equals(StringComparison.Ordinal) is manually made here.
/*if (!Options.CurrentValue.DisableAdditionalFiltering &&
    !string.Equals(await Store.GetClientIdAsync(application, cancellationToken), identifier, StringComparison.Ordinal))
{
    return null;
}*/

            return application;
        }

        /// <summary>
        /// Retrieves an application using its unique identifier.
        /// </summary>
        /// <param name="identifier">The unique identifier associated with the application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the client application corresponding to the identifier.
        /// </returns>
        public virtual async Task<TApplication> FindByIdAsync([NotNull] string identifier, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                throw new ArgumentException("The identifier cannot be null or empty.", nameof(identifier));
            }

            var application = Options.CurrentValue.DisableEntityCaching ?
                await Store.FindByIdAsync(identifier, cancellationToken) :
                await Cache.FindByIdAsync(identifier, cancellationToken);

            if (application == null)
            {
                return null;
            }

            // SQL engines like Microsoft SQL Server or MySQL are known to use case-insensitive lookups by default.
            // To ensure a case-sensitive comparison is enforced independently of the database/table/query collation
            // used by the store, a second pass using string.Equals(StringComparison.Ordinal) is manually made here.
            if (!Options.CurrentValue.DisableAdditionalFiltering &&
                !string.Equals(await Store.GetIdAsync(application, cancellationToken), identifier, StringComparison.Ordinal))
            {
                return null;
            }

            return application;
        }

        /// <summary>
        /// Retrieves all the applications associated with the specified post_logout_redirect_uri.
        /// </summary>
        /// <param name="address">The post_logout_redirect_uri associated with the applications.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation, whose result
        /// returns the client applications corresponding to the specified post_logout_redirect_uri.
        /// </returns>
        public virtual async Task<ImmutableArray<TApplication>> FindByPostLogoutRedirectUriAsync(
            [NotNull] string address, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("The address cannot be null or empty.", nameof(address));
            }

            var applications = Options.CurrentValue.DisableEntityCaching ?
                await Store.FindByPostLogoutRedirectUriAsync(address, cancellationToken) :
                await Cache.FindByPostLogoutRedirectUriAsync(address, cancellationToken);

            if (applications.IsEmpty)
            {
                return ImmutableArray.Create<TApplication>();
            }

            if (Options.CurrentValue.DisableAdditionalFiltering)
            {
                return applications;
            }

            // SQL engines like Microsoft SQL Server or MySQL are known to use case-insensitive lookups by default.
            // To ensure a case-sensitive comparison is enforced independently of the database/table/query collation
            // used by the store, a second pass using string.Equals(StringComparison.Ordinal) is manually made here.

            var builder = ImmutableArray.CreateBuilder<TApplication>(applications.Length);

            foreach (var application in applications)
            {
                foreach (var uri in await Store.GetPostLogoutRedirectUrisAsync(application, cancellationToken))
                {
                    // Note: the post_logout_redirect_uri must be compared using case-sensitive "Simple String Comparison".
                    if (string.Equals(uri, address, StringComparison.Ordinal))
                    {
                        builder.Add(application);
                    }
                }
            }

            return builder.Count == builder.Capacity ?
                builder.MoveToImmutable() :
                builder.ToImmutable();
        }

        /// <summary>
        /// Retrieves all the applications associated with the specified redirect_uri.
        /// </summary>
        /// <param name="address">The redirect_uri associated with the applications.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation, whose result
        /// returns the client applications corresponding to the specified redirect_uri.
        /// </returns>
        public virtual async Task<ImmutableArray<TApplication>> FindByRedirectUriAsync(
            [NotNull] string address, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("The address cannot be null or empty.", nameof(address));
            }

            var applications = Options.CurrentValue.DisableEntityCaching ?
                await Store.FindByRedirectUriAsync(address, cancellationToken) :
                await Cache.FindByRedirectUriAsync(address, cancellationToken);

            if (applications.IsEmpty)
            {
                return ImmutableArray.Create<TApplication>();
            }

            if (Options.CurrentValue.DisableAdditionalFiltering)
            {
                return applications;
            }

            // SQL engines like Microsoft SQL Server or MySQL are known to use case-insensitive lookups by default.
            // To ensure a case-sensitive comparison is enforced independently of the database/table/query collation
            // used by the store, a second pass using string.Equals(StringComparison.Ordinal) is manually made here.

            var builder = ImmutableArray.CreateBuilder<TApplication>(applications.Length);

            foreach (var application in applications)
            {
                foreach (var uri in await Store.GetRedirectUrisAsync(application, cancellationToken))
                {
                    // Note: the post_logout_redirect_uri must be compared using case-sensitive "Simple String Comparison".
                    if (string.Equals(uri, address, StringComparison.Ordinal))
                    {
                        builder.Add(application);
                    }
                }
            }

            return builder.Count == builder.Capacity ?
                builder.MoveToImmutable() :
                builder.ToImmutable();
        }

        /// <summary>
        /// Executes the specified query and returns the first element.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="query">The query to execute.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the first element returned when executing the query.
        /// </returns>
        public virtual Task<TResult> GetAsync<TResult>(
            [NotNull] Func<IQueryable<TApplication>, IQueryable<TResult>> query, CancellationToken cancellationToken = default)
        {
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            return GetAsync((applications, state) => state(applications), query, cancellationToken);
        }

        /// <summary>
        /// Executes the specified query and returns the first element.
        /// </summary>
        /// <typeparam name="TState">The state type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="query">The query to execute.</param>
        /// <param name="state">The optional state.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the first element returned when executing the query.
        /// </returns>
        public virtual Task<TResult> GetAsync<TState, TResult>(
            [NotNull] Func<IQueryable<TApplication>, TState, IQueryable<TResult>> query,
            [CanBeNull] TState state, CancellationToken cancellationToken = default)
        {
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            return Store.GetAsync(query, state, cancellationToken);
        }

        /// <summary>
        /// Retrieves the client identifier associated with an application.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the client identifier associated with the application.
        /// </returns>
        public virtual ValueTask<string> GetClientIdAsync(
            [NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            return Store.GetClientIdAsync(application, cancellationToken);
        }

        /// <summary>
        /// Retrieves the client type associated with an application.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the client type of the application (by default, "public").
        /// </returns>
        public virtual ValueTask<string> GetClientTypeAsync(
            [NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            return Store.GetClientTypeAsync(application, cancellationToken);
        }

        /// <summary>
        /// Retrieves the consent type associated with an application.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the consent type of the application (by default, "explicit").
        /// </returns>
        public virtual async ValueTask<string> GetConsentTypeAsync(
            [NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            var type = await Store.GetConsentTypeAsync(application, cancellationToken);
            if (string.IsNullOrEmpty(type))
            {
                return OpenIddictConstants.ConsentTypes.Explicit;
            }

            return type;
        }

        /// <summary>
        /// Retrieves the display name associated with an application.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the display name associated with the application.
        /// </returns>
        public virtual ValueTask<string> GetDisplayNameAsync(
            [NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            return Store.GetDisplayNameAsync(application, cancellationToken);
        }

        /// <summary>
        /// Retrieves the unique identifier associated with an application.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the unique identifier associated with the application.
        /// </returns>
        public virtual ValueTask<string> GetIdAsync([NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            return Store.GetIdAsync(application, cancellationToken);
        }

        /// <summary>
        /// Retrieves the permissions associated with an application.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns all the permissions associated with the application.
        /// </returns>
        public virtual ValueTask<ImmutableArray<string>> GetPermissionsAsync(
            [NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            return Store.GetPermissionsAsync(application, cancellationToken);
        }

        /// <summary>
        /// Retrieves the logout callback addresses associated with an application.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns all the post_logout_redirect_uri associated with the application.
        /// </returns>
        public virtual ValueTask<ImmutableArray<string>> GetPostLogoutRedirectUrisAsync(
            [NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            return Store.GetPostLogoutRedirectUrisAsync(application, cancellationToken);
        }

        /// <summary>
        /// Retrieves the callback addresses associated with an application.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="ValueTask{TResult}"/> that can be used to monitor the asynchronous operation,
        /// whose result returns all the redirect_uri associated with the application.
        /// </returns>
        public virtual ValueTask<ImmutableArray<string>> GetRedirectUrisAsync(
            [NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            return Store.GetRedirectUrisAsync(application, cancellationToken);
        }

        /// <summary>
        /// Determines whether the specified permission has been granted to the application.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="permission">The permission.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns><c>true</c> if the application has been granted the specified permission, <c>false</c> otherwise.</returns>
        public virtual async Task<bool> HasPermissionAsync(
            [NotNull] TApplication application, [NotNull] string permission, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            if (string.IsNullOrEmpty(permission))
            {
                throw new ArgumentException("The permission name cannot be null or empty.", nameof(permission));
            }

            return (await GetPermissionsAsync(application, cancellationToken)).Contains(permission);
        }

        /// <summary>
        /// Determines whether an application is a confidential client.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns><c>true</c> if the application is a confidential client, <c>false</c> otherwise.</returns>
        public async Task<bool> IsConfidentialAsync([NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            var type = await GetClientTypeAsync(application, cancellationToken);
            if (string.IsNullOrEmpty(type))
            {
                return false;
            }

            return string.Equals(type, OpenIddictConstants.ClientTypes.Confidential, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether an application is a hybrid client.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns><c>true</c> if the application is a hybrid client, <c>false</c> otherwise.</returns>
        public async Task<bool> IsHybridAsync([NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            var type = await GetClientTypeAsync(application, cancellationToken);
            if (string.IsNullOrEmpty(type))
            {
                return false;
            }

            return string.Equals(type, OpenIddictConstants.ClientTypes.Hybrid, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Determines whether an application is a public client.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns><c>true</c> if the application is a public client, <c>false</c> otherwise.</returns>
        public async Task<bool> IsPublicAsync([NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            // Assume client applications are public if their type is not explicitly set.
            var type = await GetClientTypeAsync(application, cancellationToken);
            if (string.IsNullOrEmpty(type))
            {
                return true;
            }

            return string.Equals(type, OpenIddictConstants.ClientTypes.Public, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Executes the specified query and returns all the corresponding elements.
        /// </summary>
        /// <param name="count">The number of results to return.</param>
        /// <param name="offset">The number of results to skip.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns all the elements returned when executing the specified query.
        /// </returns>
        public virtual Task<ImmutableArray<TApplication>> ListAsync(
            [CanBeNull] int? count = null, [CanBeNull] int? offset = null, CancellationToken cancellationToken = default)
        {
            return Store.ListAsync(count, offset, cancellationToken);
        }

        /// <summary>
        /// Executes the specified query and returns all the corresponding elements.
        /// </summary>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="query">The query to execute.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns all the elements returned when executing the specified query.
        /// </returns>
        public virtual Task<ImmutableArray<TResult>> ListAsync<TResult>(
            [NotNull] Func<IQueryable<TApplication>, IQueryable<TResult>> query, CancellationToken cancellationToken = default)
        {
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            return ListAsync((applications, state) => state(applications), query, cancellationToken);
        }

        /// <summary>
        /// Executes the specified query and returns all the corresponding elements.
        /// </summary>
        /// <typeparam name="TState">The state type.</typeparam>
        /// <typeparam name="TResult">The result type.</typeparam>
        /// <param name="query">The query to execute.</param>
        /// <param name="state">The optional state.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns all the elements returned when executing the specified query.
        /// </returns>
        public virtual Task<ImmutableArray<TResult>> ListAsync<TState, TResult>(
            [NotNull] Func<IQueryable<TApplication>, TState, IQueryable<TResult>> query,
            [CanBeNull] TState state, CancellationToken cancellationToken = default)
        {
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            return Store.ListAsync(query, state, cancellationToken);
        }

        /// <summary>
        /// Populates the application using the specified descriptor.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="descriptor">The descriptor.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual async Task PopulateAsync([NotNull] TApplication application,
            [NotNull] OpenIddictApplicationDescriptor descriptor, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            await Store.SetClientIdAsync(application, descriptor.ClientId, cancellationToken);
            await Store.SetClientSecretAsync(application, descriptor.ClientSecret, cancellationToken);
            await Store.SetClientTypeAsync(application, descriptor.Type, cancellationToken);
            await Store.SetConsentTypeAsync(application, descriptor.ConsentType, cancellationToken);
            await Store.SetDisplayNameAsync(application, descriptor.DisplayName, cancellationToken);
            await Store.SetPermissionsAsync(application, ImmutableArray.CreateRange(descriptor.Permissions), cancellationToken);
            await Store.SetPostLogoutRedirectUrisAsync(application, ImmutableArray.CreateRange(
                descriptor.PostLogoutRedirectUris.Select(address => address.OriginalString)), cancellationToken);
            await Store.SetRedirectUrisAsync(application, ImmutableArray.CreateRange(
                descriptor.RedirectUris.Select(address => address.OriginalString)), cancellationToken);
        }

        /// <summary>
        /// Populates the specified descriptor using the properties exposed by the application.
        /// </summary>
        /// <param name="descriptor">The descriptor.</param>
        /// <param name="application">The application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual async Task PopulateAsync(
            [NotNull] OpenIddictApplicationDescriptor descriptor,
            [NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            descriptor.ClientId = await Store.GetClientIdAsync(application, cancellationToken);
            descriptor.ClientSecret = await Store.GetClientSecretAsync(application, cancellationToken);
            descriptor.ConsentType = await Store.GetConsentTypeAsync(application, cancellationToken);
            descriptor.DisplayName = await Store.GetDisplayNameAsync(application, cancellationToken);
            descriptor.Type = await Store.GetClientTypeAsync(application, cancellationToken);
            descriptor.Permissions.Clear();
            descriptor.Permissions.UnionWith(await Store.GetPermissionsAsync(application, cancellationToken));
            descriptor.PostLogoutRedirectUris.Clear();
            descriptor.RedirectUris.Clear();

            foreach (var address in await Store.GetPostLogoutRedirectUrisAsync(application, cancellationToken))
            {
                // Ensure the address is not null or empty.
                if (string.IsNullOrEmpty(address))
                {
                    throw new ArgumentException("Callback URLs cannot be null or empty.");
                }

                // Ensure the address is a valid absolute URL.
                if (!Uri.TryCreate(address, UriKind.Absolute, out Uri uri) || !uri.IsWellFormedOriginalString())
                {
                    throw new ArgumentException("Callback URLs must be valid absolute URLs.");
                }

                descriptor.PostLogoutRedirectUris.Add(uri);
            }

            foreach (var address in await Store.GetRedirectUrisAsync(application, cancellationToken))
            {
                // Ensure the address is not null or empty.
                if (string.IsNullOrEmpty(address))
                {
                    throw new ArgumentException("Callback URLs cannot be null or empty.");
                }

                // Ensure the address is a valid absolute URL.
                if (!Uri.TryCreate(address, UriKind.Absolute, out Uri uri) || !uri.IsWellFormedOriginalString())
                {
                    throw new ArgumentException("Callback URLs must be valid absolute URLs.");
                }

                descriptor.RedirectUris.Add(uri);
            }
        }

        /// <summary>
        /// Updates an existing application.
        /// </summary>
        /// <param name="application">The application to update.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual async Task UpdateAsync([NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            var results = await ValidateAsync(application, cancellationToken);
            if (results.Any(result => result != ValidationResult.Success))
            {
                var builder = new StringBuilder();
                builder.AppendLine("One or more validation error(s) occurred while trying to update an existing application:");
                builder.AppendLine();

                foreach (var result in results)
                {
                    builder.AppendLine(result.ErrorMessage);
                }

                throw new OpenIddictExceptions.ValidationException(builder.ToString(), results);
            }

            await Store.UpdateAsync(application, cancellationToken);

            if (!Options.CurrentValue.DisableEntityCaching)
            {
                await Cache.RemoveAsync(application, cancellationToken);
                await Cache.AddAsync(application, cancellationToken);
            }
        }

        /// <summary>
        /// Updates an existing application and replaces the existing secret.
        /// Note: the default implementation automatically hashes the client
        /// secret before storing it in the database, for security reasons.
        /// </summary>
        /// <param name="application">The application to update.</param>
        /// <param name="secret">The client secret associated with the application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual async Task UpdateAsync([NotNull] TApplication application,
            [CanBeNull] string secret, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            if (string.IsNullOrEmpty(secret))
            {
                await Store.SetClientSecretAsync(application, null, cancellationToken);
            }

            else
            {
                secret = await ObfuscateClientSecretAsync(secret, cancellationToken);
                await Store.SetClientSecretAsync(application, secret, cancellationToken);
            }

            await UpdateAsync(application, cancellationToken);
        }

        /// <summary>
        /// Updates an existing application.
        /// </summary>
        /// <param name="application">The application to update.</param>
        /// <param name="descriptor">The descriptor used to update the application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        public virtual async Task UpdateAsync([NotNull] TApplication application,
            [NotNull] OpenIddictApplicationDescriptor descriptor, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            // Store the original client secret for later comparison.
            var comparand = await Store.GetClientSecretAsync(application, cancellationToken);
            await PopulateAsync(application, descriptor, cancellationToken);

            // If the client secret was updated, use the overload accepting a secret parameter.
            var secret = await Store.GetClientSecretAsync(application, cancellationToken);
            if (!string.Equals(secret, comparand, StringComparison.Ordinal))
            {
                await UpdateAsync(application, secret, cancellationToken);

                return;
            }

            await UpdateAsync(application, cancellationToken);
        }

        /// <summary>
        /// Validates the application to ensure it's in a consistent state.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns the validation error encountered when validating the application.
        /// </returns>
        public virtual async Task<ImmutableArray<ValidationResult>> ValidateAsync(
            [NotNull] TApplication application, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            var builder = ImmutableArray.CreateBuilder<ValidationResult>();

            // Ensure the client_id is not null or empty and is not already used for a different application.
            var identifier = await Store.GetClientIdAsync(application, cancellationToken);
            if (string.IsNullOrEmpty(identifier))
            {
                builder.Add(new ValidationResult("The client identifier cannot be null or empty."));
            }

            else
            {
                // Note: depending on the database/table/query collation used by the store, an application
                // whose client_id doesn't exactly match the specified value may be returned (e.g because
                // the casing is different). To avoid issues when the client identifier is part of an index
                // using the same collation, an error is added even if the two identifiers don't exactly match.
                var other = await Store.FindByClientIdAsync(identifier, cancellationToken);
                if (other != null && !string.Equals(
                    await Store.GetIdAsync(other, cancellationToken),
                    await Store.GetIdAsync(application, cancellationToken), StringComparison.Ordinal))
                {
                    builder.Add(new ValidationResult("An application with the same client identifier already exists."));
                }
            }

            var type = await Store.GetClientTypeAsync(application, cancellationToken);
            if (string.IsNullOrEmpty(type))
            {
                builder.Add(new ValidationResult("The client type cannot be null or empty."));
            }

            else
            {
                // Ensure the application type is supported by the manager.
                if (!string.Equals(type, OpenIddictConstants.ClientTypes.Confidential, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(type, OpenIddictConstants.ClientTypes.Hybrid, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(type, OpenIddictConstants.ClientTypes.Public, StringComparison.OrdinalIgnoreCase))
                {
                    builder.Add(new ValidationResult("Only 'confidential', 'hybrid' or 'public' applications are " +
                                                     "supported by the default application manager."));
                }

                // Ensure a client secret was specified if the client is a confidential application.
                var secret = await Store.GetClientSecretAsync(application, cancellationToken);
                if (string.IsNullOrEmpty(secret) &&
                    string.Equals(type, OpenIddictConstants.ClientTypes.Confidential, StringComparison.OrdinalIgnoreCase))
                {
                    builder.Add(new ValidationResult("The client secret cannot be null or empty for a confidential application."));
                }

                // Ensure no client secret was specified if the client is a public application.
                else if (!string.IsNullOrEmpty(secret) &&
                          string.Equals(type, OpenIddictConstants.ClientTypes.Public, StringComparison.OrdinalIgnoreCase))
                {
                    builder.Add(new ValidationResult("A client secret cannot be associated with a public application."));
                }
            }

            // When callback URLs are specified, ensure they are valid and spec-compliant.
            // See https://tools.ietf.org/html/rfc6749#section-3.1 for more information.
            foreach (var address in ImmutableArray.Create<string>()
                .AddRange(await Store.GetPostLogoutRedirectUrisAsync(application, cancellationToken))
                .AddRange(await Store.GetRedirectUrisAsync(application, cancellationToken)))
            {
                // Ensure the address is not null or empty.
                if (string.IsNullOrEmpty(address))
                {
                    builder.Add(new ValidationResult("Callback URLs cannot be null or empty."));

                    break;
                }

                // Ensure the address is a valid absolute URL.
                if (!Uri.TryCreate(address, UriKind.Absolute, out Uri uri) || !uri.IsWellFormedOriginalString())
                {
                    builder.Add(new ValidationResult("Callback URLs must be valid absolute URLs."));

                    break;
                }

                // Ensure the address doesn't contain a fragment.
                if (!string.IsNullOrEmpty(uri.Fragment))
                {
                    builder.Add(new ValidationResult("Callback URLs cannot contain a fragment."));

                    break;
                }
            }

            return builder.Count == builder.Capacity ?
                builder.MoveToImmutable() :
                builder.ToImmutable();
        }

        /// <summary>
        /// Validates the client_secret associated with an application.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="secret">The secret that should be compared to the client_secret stored in the database.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>A <see cref="Task"/> that can be used to monitor the asynchronous operation.</returns>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns a boolean indicating whether the client secret was valid.
        /// </returns>
        public virtual async Task<bool> ValidateClientSecretAsync(
            [NotNull] TApplication application, string secret, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            if (await IsPublicAsync(application, cancellationToken))
            {
                Logger.LogWarning("Client authentication cannot be enforced for public applications.");

                return false;
            }

            var value = await Store.GetClientSecretAsync(application, cancellationToken);
            if (string.IsNullOrEmpty(value))
            {
                Logger.LogError("Client authentication failed for {Client} because " +
                                "no client secret was associated with the application.");

                return false;
            }

            if (!await ValidateClientSecretAsync(secret, value, cancellationToken))
            {
                Logger.LogWarning("Client authentication failed for {Client}.",
                    await GetClientIdAsync(application, cancellationToken));

                return false;
            }

            return true;
        }

        /// <summary>
        /// Validates the redirect_uri to ensure it's associated with an application.
        /// </summary>
        /// <param name="application">The application.</param>
        /// <param name="address">The address that should be compared to one of the redirect_uri stored in the database.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns a boolean indicating whether the redirect_uri was valid.
        /// </returns>
        public virtual async Task<bool> ValidateRedirectUriAsync(
            [NotNull] TApplication application, [NotNull] string address, CancellationToken cancellationToken = default)
        {
            if (application == null)
            {
                throw new ArgumentNullException(nameof(application));
            }

            if (string.IsNullOrEmpty(address))
            {
                throw new ArgumentException("The address cannot be null or empty.", nameof(address));
            }

            foreach (var uri in await Store.GetRedirectUrisAsync(application, cancellationToken))
            {
                // Note: the redirect_uri must be compared using case-sensitive "Simple String Comparison".
                // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest for more information.
                if (string.Equals(uri, address, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            Logger.LogWarning("Client validation failed because '{RedirectUri}' was not a valid redirect_uri " +
                              "for {Client}.", address, await GetClientIdAsync(application, cancellationToken));

            return false;
        }

        /// <summary>
        /// Obfuscates the specified client secret so it can be safely stored in a database.
        /// By default, this method returns a complex hashed representation computed using PBKDF2.
        /// </summary>
        /// <param name="secret">The client secret.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation.
        /// </returns>
        protected virtual Task<string> ObfuscateClientSecretAsync([NotNull] string secret, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(secret))
            {
                throw new ArgumentException("The secret cannot be null or empty.", nameof(secret));
            }

            return Task.FromResult(Crypto.HashPassword(secret));
        }

        /// <summary>
        /// Validates the specified value to ensure it corresponds to the client secret.
        /// Note: when overriding this method, using a time-constant comparer is strongly recommended.
        /// </summary>
        /// <param name="secret">The client secret to compare to the value stored in the database.</param>
        /// <param name="comparand">The value stored in the database, which is usually a hashed representation of the secret.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> that can be used to abort the operation.</param>
        /// <returns>
        /// A <see cref="Task"/> that can be used to monitor the asynchronous operation,
        /// whose result returns a boolean indicating whether the specified value was valid.
        /// </returns>
        protected virtual Task<bool> ValidateClientSecretAsync(
            [NotNull] string secret, [NotNull] string comparand, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(secret))
            {
                throw new ArgumentException("The secret cannot be null or empty.", nameof(secret));
            }

            if (string.IsNullOrEmpty(comparand))
            {
                throw new ArgumentException("The comparand cannot be null or empty.", nameof(comparand));
            }

            try
            {
                return Task.FromResult(Crypto.VerifyHashedPassword(comparand, secret));
            }

            catch (Exception exception)
            {
                Logger.LogWarning(exception, "An error occurred while trying to verify a client secret. " +
                                             "This may indicate that the hashed entry is corrupted or malformed.");

                return Task.FromResult(false);
            }
        }

        Task<long> IOpenIddictApplicationManager.CountAsync(CancellationToken cancellationToken)
            => CountAsync(cancellationToken);

        Task<long> IOpenIddictApplicationManager.CountAsync<TResult>(Func<IQueryable<object>, IQueryable<TResult>> query, CancellationToken cancellationToken)
            => CountAsync(query, cancellationToken);

        async Task<object> IOpenIddictApplicationManager.CreateAsync(OpenIddictApplicationDescriptor descriptor, CancellationToken cancellationToken)
            => await CreateAsync(descriptor, cancellationToken);

        Task IOpenIddictApplicationManager.CreateAsync(object application, CancellationToken cancellationToken)
            => CreateAsync((TApplication) application, cancellationToken);

        Task IOpenIddictApplicationManager.CreateAsync(object application, string secret, CancellationToken cancellationToken)
            => CreateAsync((TApplication) application, secret, cancellationToken);

        Task IOpenIddictApplicationManager.DeleteAsync(object application, CancellationToken cancellationToken)
            => DeleteAsync((TApplication) application, cancellationToken);

        async Task<object> IOpenIddictApplicationManager.FindByClientIdAsync(string identifier, CancellationToken cancellationToken)
            => await FindByClientIdAsync(identifier, cancellationToken);

        async Task<object> IOpenIddictApplicationManager.FindByIdAsync(string identifier, CancellationToken cancellationToken)
            => await FindByIdAsync(identifier, cancellationToken);

        async Task<ImmutableArray<object>> IOpenIddictApplicationManager.FindByPostLogoutRedirectUriAsync(string address, CancellationToken cancellationToken)
            => (await FindByPostLogoutRedirectUriAsync(address, cancellationToken)).CastArray<object>();

        async Task<ImmutableArray<object>> IOpenIddictApplicationManager.FindByRedirectUriAsync(string address, CancellationToken cancellationToken)
            => (await FindByRedirectUriAsync(address, cancellationToken)).CastArray<object>();

        Task<TResult> IOpenIddictApplicationManager.GetAsync<TResult>(Func<IQueryable<object>, IQueryable<TResult>> query, CancellationToken cancellationToken)
            => GetAsync(query, cancellationToken);

        Task<TResult> IOpenIddictApplicationManager.GetAsync<TState, TResult>(Func<IQueryable<object>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken)
            => GetAsync(query, state, cancellationToken);

        ValueTask<string> IOpenIddictApplicationManager.GetClientIdAsync(object application, CancellationToken cancellationToken)
            => GetClientIdAsync((TApplication) application, cancellationToken);

        ValueTask<string> IOpenIddictApplicationManager.GetClientTypeAsync(object application, CancellationToken cancellationToken)
            => GetClientTypeAsync((TApplication) application, cancellationToken);

        ValueTask<string> IOpenIddictApplicationManager.GetConsentTypeAsync(object application, CancellationToken cancellationToken)
            => GetConsentTypeAsync((TApplication) application, cancellationToken);

        ValueTask<string> IOpenIddictApplicationManager.GetDisplayNameAsync(object application, CancellationToken cancellationToken)
            => GetDisplayNameAsync((TApplication) application, cancellationToken);

        ValueTask<string> IOpenIddictApplicationManager.GetIdAsync(object application, CancellationToken cancellationToken)
            => GetIdAsync((TApplication) application, cancellationToken);

        ValueTask<ImmutableArray<string>> IOpenIddictApplicationManager.GetPermissionsAsync(object application, CancellationToken cancellationToken)
            => GetPermissionsAsync((TApplication) application, cancellationToken);

        ValueTask<ImmutableArray<string>> IOpenIddictApplicationManager.GetPostLogoutRedirectUrisAsync(object application, CancellationToken cancellationToken)
            => GetPostLogoutRedirectUrisAsync((TApplication) application, cancellationToken);

        ValueTask<ImmutableArray<string>> IOpenIddictApplicationManager.GetRedirectUrisAsync(object application, CancellationToken cancellationToken)
            => GetRedirectUrisAsync((TApplication) application, cancellationToken);

        Task<bool> IOpenIddictApplicationManager.HasPermissionAsync(object application, string permission, CancellationToken cancellationToken)
            => HasPermissionAsync((TApplication) application, permission, cancellationToken);

        Task<bool> IOpenIddictApplicationManager.IsConfidentialAsync(object application, CancellationToken cancellationToken)
            => IsConfidentialAsync((TApplication) application, cancellationToken);

        Task<bool> IOpenIddictApplicationManager.IsHybridAsync(object application, CancellationToken cancellationToken)
            => IsHybridAsync((TApplication) application, cancellationToken);

        Task<bool> IOpenIddictApplicationManager.IsPublicAsync(object application, CancellationToken cancellationToken)
            => IsPublicAsync((TApplication) application, cancellationToken);

        async Task<ImmutableArray<object>> IOpenIddictApplicationManager.ListAsync(int? count, int? offset, CancellationToken cancellationToken)
            => (await ListAsync(count, offset, cancellationToken)).CastArray<object>();

        Task<ImmutableArray<TResult>> IOpenIddictApplicationManager.ListAsync<TResult>(Func<IQueryable<object>, IQueryable<TResult>> query, CancellationToken cancellationToken)
            => ListAsync(query, cancellationToken);

        Task<ImmutableArray<TResult>> IOpenIddictApplicationManager.ListAsync<TState, TResult>(Func<IQueryable<object>, TState, IQueryable<TResult>> query, TState state, CancellationToken cancellationToken)
            => ListAsync(query, state, cancellationToken);

        Task IOpenIddictApplicationManager.PopulateAsync(OpenIddictApplicationDescriptor descriptor, object application, CancellationToken cancellationToken)
            => PopulateAsync(descriptor, (TApplication) application, cancellationToken);

        Task IOpenIddictApplicationManager.PopulateAsync(object application, OpenIddictApplicationDescriptor descriptor, CancellationToken cancellationToken)
            => PopulateAsync((TApplication) application, descriptor, cancellationToken);

        Task IOpenIddictApplicationManager.UpdateAsync(object application, CancellationToken cancellationToken)
            => UpdateAsync((TApplication) application, cancellationToken);

        Task IOpenIddictApplicationManager.UpdateAsync(object application, OpenIddictApplicationDescriptor descriptor, CancellationToken cancellationToken)
            => UpdateAsync((TApplication) application, descriptor, cancellationToken);

        Task IOpenIddictApplicationManager.UpdateAsync(object application, string secret, CancellationToken cancellationToken)
            => UpdateAsync((TApplication) application, secret, cancellationToken);

        Task<ImmutableArray<ValidationResult>> IOpenIddictApplicationManager.ValidateAsync(object application, CancellationToken cancellationToken)
            => ValidateAsync((TApplication) application, cancellationToken);

        Task<bool> IOpenIddictApplicationManager.ValidateClientSecretAsync(object application, string secret, CancellationToken cancellationToken)
            => ValidateClientSecretAsync((TApplication) application, secret, cancellationToken);

        Task<bool> IOpenIddictApplicationManager.ValidateRedirectUriAsync(object application, string address, CancellationToken cancellationToken)
            => ValidateRedirectUriAsync((TApplication) application, address, cancellationToken);
    }
}