using ErsatzTV.Application.MediaItems;

namespace ErsatzTV.Application.Search;

public record SearchTelevisionSeasons(string Query) : IRequest<List<NamedMediaItemViewModel>>;
