using System;
using System.Collections.Generic;
using System.Linq;
using Bizgo.Models;

namespace Bizgo
{
    /// <summary>
    /// Recipients of a send: one phone number, several numbers, or <see cref="Destination"/> objects (for <c>ReplaceWords</c>
    /// substitution or a per-recipient <c>Ref</c>). Max 200 per request. Converts implicitly from <see cref="string"/>,
    /// <c>string[]</c>, <c>List&lt;string&gt;</c>, <see cref="Destination"/>, <c>Destination[]</c> and <c>List&lt;Destination&gt;</c>;
    /// use <see cref="Of(IEnumerable{string})"/> for other sequences.
    /// </summary>
    public sealed class Recipients
    {
        private Recipients(IReadOnlyList<Destination> destinations)
        {
            Destinations = destinations;
        }

        /// <summary>The recipients as <see cref="Destination"/> objects (strings are wrapped).</summary>
        public IReadOnlyList<Destination> Destinations { get; }

        /// <summary>One phone number.</summary>
        /// <param name="to">Phone number.</param>
        public static implicit operator Recipients(string to) => Of(to);

        /// <summary>Several phone numbers.</summary>
        /// <param name="to">Phone numbers.</param>
        public static implicit operator Recipients(string[] to) => Of(to);

        /// <summary>Several phone numbers.</summary>
        /// <param name="to">Phone numbers.</param>
        public static implicit operator Recipients(List<string> to) => Of(to);

        /// <summary>One recipient.</summary>
        /// <param name="to">Recipient.</param>
        public static implicit operator Recipients(Destination to) => Of(to);

        /// <summary>Several recipients.</summary>
        /// <param name="to">Recipients.</param>
        public static implicit operator Recipients(Destination[] to) => Of(to);

        /// <summary>Several recipients.</summary>
        /// <param name="to">Recipients.</param>
        public static implicit operator Recipients(List<Destination> to) => Of(to);

        /// <summary>One phone number.</summary>
        /// <param name="to">Phone number.</param>
        /// <returns>Recipients.</returns>
        public static Recipients Of(string to)
        {
            if (to is null)
            {
                throw new ArgumentNullException(nameof(to));
            }

            return new Recipients(new[] { new Destination(to) });
        }

        /// <summary>Several phone numbers.</summary>
        /// <param name="to">Phone numbers.</param>
        /// <returns>Recipients.</returns>
        public static Recipients Of(IEnumerable<string> to)
        {
            if (to is null)
            {
                throw new ArgumentNullException(nameof(to));
            }

            return new Recipients(to.Select(n => n is null ? null! : new Destination(n)).ToArray());
        }

        /// <summary>One recipient.</summary>
        /// <param name="to">Recipient.</param>
        /// <returns>Recipients.</returns>
        public static Recipients Of(Destination to)
        {
            if (to is null)
            {
                throw new ArgumentNullException(nameof(to));
            }

            return new Recipients(new[] { to });
        }

        /// <summary>Several recipients.</summary>
        /// <param name="to">Recipients.</param>
        /// <returns>Recipients.</returns>
        public static Recipients Of(IEnumerable<Destination> to)
        {
            if (to is null)
            {
                throw new ArgumentNullException(nameof(to));
            }

            return new Recipients(to.ToArray());
        }

        /// <inheritdoc/>
        public override string ToString() => "Recipients(Count=" + Destinations.Count + ")";
    }

    /// <summary>Optional request-level fields of a send.</summary>
    public sealed class SendOptions
    {
        /// <summary>Your reference value; returned in reports.</summary>
        public string? Ref { get; set; }

        /// <summary>Groups messages in message insight statistics.</summary>
        public string? GroupKey { get; set; }

        /// <summary>Department code for billing.</summary>
        public string? PaymentCode { get; set; }

        /// <summary>
        /// Up to 200 characters. Set it to make retries safe: a resend with the same key within <see cref="IdempotencyTtl"/> seconds is
        /// rejected with <see cref="BizgoDuplicateRequestException"/> instead of being delivered twice. It also enables automatic
        /// retries after timeouts and 5xx responses.
        /// </summary>
        public string? IdempotencyKey { get; set; }

        /// <summary>Seconds (0-86400) the idempotency key is remembered. Null with <see cref="IdempotencyKey"/> set sends <see cref="BizgoClient.DefaultIdempotencyTtl"/> (86400); 0 is sent as is.</summary>
        public int? IdempotencyTtl { get; set; }
    }
}
